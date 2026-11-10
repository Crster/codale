#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "llama.h"

// No CRT dependency: cross-compile with the Windows SDK, including ARM64.
extern "C" int _fltused = 0;
extern "C" void* memcpy(void*, const void*, size_t);
extern "C" void* memset(void*, int, size_t);
#pragma function(memcpy, memset)
extern "C" void* memcpy(void* dest, const void* src, size_t size) {
    auto d = (volatile unsigned char*)dest; auto s = (const volatile unsigned char*)src;
    while (size--) *d++ = *s++; return dest;
}
extern "C" void* memset(void* dest, int value, size_t size) {
    auto d = (volatile unsigned char*)dest; while (size--) *d++ = (unsigned char)value; return dest;
}
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
static void* alloc(size_t size) { return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, size); }
static void release(void* ptr) { if (ptr) HeapFree(GetProcessHeap(), 0, ptr); }
static int smaller(int a, int b) { return a < b ? a : b; }
static bool same(const char* a, const char* b, int size) { for (int i = 0; i < size; ++i) if (a[i] != b[i]) return false; return true; }
static void error(char* dest, int size, const char* text) { if (size > 0) lstrcpynA(dest, text, size); }
static HMODULE llama, ggml;
#define API(name) static decltype(&name) p_##name
API(llama_model_default_params); API(llama_context_default_params);
API(llama_model_load_from_file); API(llama_model_free); API(llama_init_from_model); API(llama_free);
API(llama_model_get_vocab); API(llama_tokenize); API(llama_token_to_piece); API(llama_vocab_is_eog);
API(llama_decode); API(llama_batch_get_one); API(llama_get_memory); API(llama_memory_clear); API(llama_set_abort_callback);
API(llama_sampler_init_greedy); API(llama_sampler_sample); API(llama_sampler_free);
API(llama_sampler_chain_init); API(llama_sampler_chain_default_params); API(llama_sampler_chain_add); API(llama_sampler_init_grammar);
API(llama_sampler_init_top_p); API(llama_sampler_init_temp); API(llama_sampler_init_dist); API(llama_memory_seq_rm);
API(ggml_backend_load_all_from_path); API(ggml_backend_dev_by_type); API(llama_log_set); API(ggml_log_set);
API(ggml_backend_dev_count); API(ggml_backend_dev_get); API(ggml_backend_dev_type); API(ggml_backend_dev_memory); API(ggml_backend_dev_description);
static void quiet(enum ggml_log_level, const char*, void*) { }
static bool bind(const char* folder, char* err, int err_size) {
    if (llama) return true;
    static wchar_t dir[32768], file[32768];
    if (!MultiByteToWideChar(CP_UTF8, 0, folder, -1, dir, 32700)) { error(err, err_size, "Invalid native folder."); return false; }
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
    AddDllDirectory(dir);
    lstrcpyW(file, dir); lstrcatW(file, L"/ggml.dll");
    ggml = LoadLibraryExW(file, nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    lstrcpyW(file, dir); lstrcatW(file, L"/llama.dll");
    auto module = LoadLibraryExW(file, nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!ggml || !module) { error(err, err_size, "Cannot load bundled llama.cpp DLLs."); return false; }
#define LOAD(mod, name) p_##name = reinterpret_cast<decltype(p_##name)>(GetProcAddress(mod, #name)); if (!p_##name) { error(err, err_size, "Missing native export: " #name); return false; }
    LOAD(module, llama_model_default_params); LOAD(module, llama_context_default_params);
    LOAD(module, llama_model_load_from_file); LOAD(module, llama_model_free); LOAD(module, llama_init_from_model); LOAD(module, llama_free);
    LOAD(module, llama_model_get_vocab); LOAD(module, llama_tokenize); LOAD(module, llama_token_to_piece); LOAD(module, llama_vocab_is_eog);
    LOAD(module, llama_decode); LOAD(module, llama_batch_get_one); LOAD(module, llama_get_memory); LOAD(module, llama_memory_clear); LOAD(module, llama_set_abort_callback);
    LOAD(module, llama_sampler_init_greedy); LOAD(module, llama_sampler_sample); LOAD(module, llama_sampler_free);
    LOAD(module, llama_sampler_chain_init); LOAD(module, llama_sampler_chain_default_params); LOAD(module, llama_sampler_chain_add); LOAD(module, llama_sampler_init_grammar);
    LOAD(module, llama_sampler_init_top_p); LOAD(module, llama_sampler_init_temp); LOAD(module, llama_sampler_init_dist); LOAD(module, llama_memory_seq_rm);
    LOAD(ggml, ggml_backend_load_all_from_path); LOAD(ggml, ggml_backend_dev_by_type);
    LOAD(ggml, ggml_backend_dev_count); LOAD(ggml, ggml_backend_dev_get);
    auto base = GetModuleHandleW(L"ggml-base.dll");
    LOAD(base, ggml_backend_dev_type); LOAD(base, ggml_backend_dev_memory); LOAD(base, ggml_backend_dev_description);
    LOAD(module, llama_log_set); LOAD(GetModuleHandleW(L"ggml-base.dll"), ggml_log_set);
    p_llama_log_set(quiet, nullptr); p_ggml_log_set(quiet, nullptr);
    p_ggml_backend_load_all_from_path(folder); llama = module; return true;
}
// Context size shared with SmartConversation.ContextTokens.
static const int context_tokens = 16384;
// cached: the tokens whose KV entries are in the context, so a prompt that shares a prefix with the
// previous one (the same system prompt, the next tool round) only evaluates its tail.
struct runtime { llama_model* model; llama_context* context; llama_token* cached; int cached_count; };
typedef int (__cdecl *cancel_fn)(void*);
struct cancellation { cancel_fn fn; void* state; };
static bool aborted(void* p) { auto c = (cancellation*)p; return c->fn(c->state) != 0; }
static bool loading(float, void* p) { return !aborted(p); }
extern "C" __declspec(dllexport) void __cdecl smart_close(runtime* r) {
    if (!r) return; release(r->cached); if (r->context) p_llama_free(r->context); if (r->model) p_llama_model_free(r->model); release(r);
}
// The one GPU the model runs on. Splitting layers across a discrete GPU and an integrated one (which reports shared
// system memory as its own, e.g. a Radeon 610M beside an RTX 5050) makes every token wait on the slow device, so
// only the discrete GPU with the most memory is used; an integrated GPU only when it is the sole GPU.
static ggml_backend_dev_t best_gpu() {
    ggml_backend_dev_t best = nullptr; int best_rank = 0; size_t best_total = 0;
    for (size_t i = 0; i < p_ggml_backend_dev_count(); ++i) {
        auto dev = p_ggml_backend_dev_get(i); auto type = p_ggml_backend_dev_type(dev);
        int rank = type == GGML_BACKEND_DEVICE_TYPE_GPU ? 2 : type == GGML_BACKEND_DEVICE_TYPE_IGPU ? 1 : 0;
        if (!rank) continue;
        size_t free = 0, total = 0; p_ggml_backend_dev_memory(dev, &free, &total);
        if (rank > best_rank || (rank == best_rank && total > best_total)) { best = dev; best_rank = rank; best_total = total; }
    }
    return best;
}
static void append(char* out, int capacity, int& length, const char* text) {
    while (*text && length + 1 < capacity) out[length++] = *text++;
    if (capacity > 0) out[length] = 0;
}
static void append_number(char* out, int capacity, int& length, unsigned long long value) {
    char digits[24]; int n = 0; do { digits[n++] = (char)('0' + value % 10); value /= 10; } while (value);
    char text[24]; for (int i = 0; i < n; ++i) text[i] = digits[n - 1 - i]; text[n] = 0; append(out, capacity, length, text);
}
// Diagnostics: one line per backend device, "type<TAB>total MiB<TAB>free MiB<TAB>description", the chosen GPU marked "*".
extern "C" __declspec(dllexport) int __cdecl smart_devices(const char* folder, char* out, int capacity, char* err, int err_size) {
    if (!bind(folder, err, err_size)) return -1;
    int length = 0; auto chosen = best_gpu(); if (capacity > 0) out[0] = 0;
    for (size_t i = 0; i < p_ggml_backend_dev_count(); ++i) {
        auto dev = p_ggml_backend_dev_get(i); auto type = p_ggml_backend_dev_type(dev);
        size_t free = 0, total = 0; p_ggml_backend_dev_memory(dev, &free, &total);
        append(out, capacity, length, dev == chosen ? "*" : "");
        append(out, capacity, length, type == GGML_BACKEND_DEVICE_TYPE_GPU ? "gpu" : type == GGML_BACKEND_DEVICE_TYPE_IGPU ? "igpu" : type == GGML_BACKEND_DEVICE_TYPE_CPU ? "cpu" : "other");
        append(out, capacity, length, "\t"); append_number(out, capacity, length, total >> 20);
        append(out, capacity, length, "\t"); append_number(out, capacity, length, free >> 20);
        append(out, capacity, length, "\t"); append(out, capacity, length, p_ggml_backend_dev_description(dev));
        append(out, capacity, length, "\n");
    }
    return length;
}
extern "C" __declspec(dllexport) runtime* __cdecl smart_open(const char* folder, const char* path, int gpu,
    cancel_fn cancel, void* state, char* err, int err_size) {
    if (!bind(folder, err, err_size)) return nullptr;
    ggml_backend_dev_t devices[2] = { gpu ? best_gpu() : nullptr, nullptr };
    if (gpu && !devices[0]) { error(err, err_size, "No Vulkan GPU available."); return nullptr; }
    auto r = (runtime*)alloc(sizeof(runtime));
    if (!r) { error(err, err_size, "Out of memory."); return nullptr; }
    cancellation c{cancel, state};
    auto mp = p_llama_model_default_params(); mp.n_gpu_layers = gpu ? 99 : 0;
    if (gpu) { mp.devices = devices; mp.split_mode = LLAMA_SPLIT_MODE_NONE; mp.main_gpu = 0; }
    mp.progress_callback = loading; mp.progress_callback_user_data = &c;
    r->model = p_llama_model_load_from_file(path, mp);
    if (!r->model) { error(err, err_size, "Model loading failed (memory, model, or driver)."); smart_close(r); return nullptr; }
    if (cancel(state)) { smart_close(r); return nullptr; }
    auto cp = p_llama_context_default_params(); cp.n_ctx = context_tokens; cp.n_batch = 2048; cp.n_ubatch = 512;
    auto threads = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) / 2;
    cp.n_threads = cp.n_threads_batch = threads > 0 ? (int)threads : 1;
    cp.offload_kqv = cp.op_offload = gpu != 0;
    // Flash attention and a q8_0 KV cache halve the cache (2.6 GB -> 1.3 GB at 16K for Granite 4.2 8B), so
    // weights and cache fit an 8 GB GPU. A quantized V cache needs flash attention: otherwise fall back to f16.
    cp.flash_attn_type = LLAMA_FLASH_ATTN_TYPE_AUTO; cp.type_k = cp.type_v = GGML_TYPE_Q8_0;    r->context = p_llama_init_from_model(r->model, cp);
    if (!r->context) { cp.type_k = cp.type_v = GGML_TYPE_F16; r->context = p_llama_init_from_model(r->model, cp); }
    if (!r->context) { error(err, err_size, "Cannot allocate inference context."); smart_close(r); return nullptr; }
    r->cached = (llama_token*)alloc(context_tokens * sizeof(llama_token));
    if (!r->cached) { error(err, err_size, "Out of memory."); smart_close(r); return nullptr; }
    return r;
}
extern "C" __declspec(dllexport) int __cdecl smart_token_count(runtime* r, const char* prompt) {
    return -p_llama_tokenize(p_llama_model_get_vocab(r->model), prompt, lstrlenA(prompt), nullptr, 0, true, true);
}
// Index of the first stop string in output[from..length), or -1. stops is a double-NUL-terminated list.
// A stop only counts after visible text, so a completion that opens with a line break is not cut to nothing.
static int find_stop(const char* output, int length, int from, const char* stops) {
    if (!stops) return -1;
    int visible = 0;
    while (visible < length && (output[visible] == ' ' || output[visible] == '\t' || output[visible] == '\r' || output[visible] == '\n')) ++visible;
    if (from < visible) from = visible;
    int best = -1;
    for (auto stop = stops; *stop; stop += lstrlenA(stop) + 1) {
        int size = lstrlenA(stop);
        for (int at = from; at + size <= length; ++at)
            if (same(output + at, stop, size)) { if (best < 0 || at < best) best = at; break; }
    }
    return best;
}
static int generate(runtime* r, const char* prompt, int max_tokens, float temperature, float top_p, unsigned seed,
    const char* grammar, const char* stops, cancel_fn cancel, void* state, char* output, int capacity, char* err, int err_size) {
    const char* failure = nullptr; int length = 0;
    auto vocab = p_llama_model_get_vocab(r->model);
    int count = -p_llama_tokenize(vocab, prompt, lstrlenA(prompt), nullptr, 0, true, true);
    if (count <= 0 || count + max_tokens > context_tokens) { error(err, err_size, "Input exceeds the model context budget."); return -1; }
    auto tokens = (llama_token*)alloc(count * sizeof(llama_token));
    if (!tokens) { error(err, err_size, "Out of memory."); return -1; }
    p_llama_tokenize(vocab, prompt, lstrlenA(prompt), tokens, count, true, true);
    // Keep the KV entries of the longest prefix shared with the previous call; at least one token is
    // evaluated so the logits are fresh.
    int keep = 0;
    while (keep < r->cached_count && keep < count - 1 && r->cached[keep] == tokens[keep]) ++keep;
    if (keep == 0 || !p_llama_memory_seq_rm(p_llama_get_memory(r->context), 0, keep, -1)) {
        p_llama_memory_clear(p_llama_get_memory(r->context), true); keep = 0;
    }
    r->cached_count = keep;
    cancellation c{cancel, state}; p_llama_set_abort_callback(r->context, aborted, &c);
    auto sampler = p_llama_sampler_chain_init(p_llama_sampler_chain_default_params());
    if (grammar && *grammar) {
        auto constrained = p_llama_sampler_init_grammar(vocab, grammar, "root");
        if (!constrained) failure = "Invalid output grammar.";
        else p_llama_sampler_chain_add(sampler, constrained);
    }
    if (temperature > 0) {
        p_llama_sampler_chain_add(sampler, p_llama_sampler_init_top_p(top_p > 0 && top_p < 1 ? top_p : 1.0f, 1));
        p_llama_sampler_chain_add(sampler, p_llama_sampler_init_temp(temperature));
        p_llama_sampler_chain_add(sampler, p_llama_sampler_init_dist(seed));
    }
    else p_llama_sampler_chain_add(sampler, p_llama_sampler_init_greedy());
    for (int i = keep; !failure && i < count; i += 512) {
        int size = smaller(512, count - i);
        if (cancel(state)) failure = "Cancelled.";
        else if (p_llama_decode(r->context, p_llama_batch_get_one(tokens + i, size))) failure = "Prompt evaluation failed.";
        else { memcpy(r->cached + i, tokens + i, size * sizeof(llama_token)); r->cached_count = i + size; }
    }
    for (int i = 0; !failure && i < max_tokens; ++i) {
        if (cancel(state)) { failure = "Cancelled."; break; }
        auto token = p_llama_sampler_sample(sampler, r->context, -1);
        if (p_llama_vocab_is_eog(vocab, token)) break;
        char piece[512]; int bytes = p_llama_token_to_piece(vocab, token, piece, sizeof(piece), 0, false);
        if (bytes < 0 || length + bytes >= capacity) { failure = "Output buffer exceeded."; break; }
        int from = length > 64 ? length - 64 : 0;
        memcpy(output + length, piece, bytes); length += bytes;
        int stop = find_stop(output, length, from, stops);
        if (stop >= 0) { length = stop; break; }
        if (p_llama_decode(r->context, p_llama_batch_get_one(&token, 1))) { failure = "Generation failed."; break; }
        if (r->cached_count < context_tokens) r->cached[r->cached_count++] = token;
    }
    p_llama_set_abort_callback(r->context, nullptr, nullptr);
    p_llama_sampler_free(sampler); release(tokens);
    if (failure) { r->cached_count = 0; error(err, err_size, failure); return -1; }
    output[length] = 0; return length;
}
extern "C" __declspec(dllexport) int __cdecl smart_generate_v2(runtime* r, const char* prompt, int max_tokens, int protocol,
    const char* grammar, cancel_fn cancel, void* state, char* output, int capacity, char* err, int err_size) {
    return generate(r, prompt, max_tokens, 0, 1, LLAMA_DEFAULT_SEED, grammar, nullptr, cancel, state, output, capacity, err, err_size);
}
// temperature <= 0 decodes greedily; seed LLAMA_DEFAULT_SEED is random; stops is a double-NUL-terminated list or null.
extern "C" __declspec(dllexport) int __cdecl smart_generate_v3(runtime* r, const char* prompt, int max_tokens, float temperature,
    float top_p, unsigned seed, const char* grammar, const char* stops, cancel_fn cancel, void* state, char* output, int capacity,
    char* err, int err_size) {
    return generate(r, prompt, max_tokens, temperature, top_p, seed, grammar, stops, cancel, state, output, capacity, err, err_size);
}
