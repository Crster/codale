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
API(ggml_backend_load_all_from_path); API(ggml_backend_dev_by_type); API(llama_log_set); API(ggml_log_set);
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
    LOAD(ggml, ggml_backend_load_all_from_path); LOAD(ggml, ggml_backend_dev_by_type);
    LOAD(module, llama_log_set); LOAD(GetModuleHandleW(L"ggml-base.dll"), ggml_log_set);
    p_llama_log_set(quiet, nullptr); p_ggml_log_set(quiet, nullptr);
    p_ggml_backend_load_all_from_path(folder); llama = module; return true;
}
struct runtime { llama_model* model; llama_context* context; };
typedef int (__cdecl *cancel_fn)(void*);
struct cancellation { cancel_fn fn; void* state; };
static bool aborted(void* p) { auto c = (cancellation*)p; return c->fn(c->state) != 0; }
static bool loading(float, void* p) { return !aborted(p); }
extern "C" __declspec(dllexport) void __cdecl smart_close(runtime* r) {
    if (!r) return; if (r->context) p_llama_free(r->context); if (r->model) p_llama_model_free(r->model); release(r);
}
extern "C" __declspec(dllexport) runtime* __cdecl smart_open(const char* folder, const char* path, int gpu,
    cancel_fn cancel, void* state, char* err, int err_size) {
    if (!bind(folder, err, err_size)) return nullptr;
    if (gpu && !p_ggml_backend_dev_by_type(GGML_BACKEND_DEVICE_TYPE_GPU)) { error(err, err_size, "No Vulkan GPU available."); return nullptr; }
    auto r = (runtime*)alloc(sizeof(runtime));
    if (!r) { error(err, err_size, "Out of memory."); return nullptr; }
    cancellation c{cancel, state};
    auto mp = p_llama_model_default_params(); mp.n_gpu_layers = gpu ? 99 : 0;
    mp.progress_callback = loading; mp.progress_callback_user_data = &c;
    r->model = p_llama_model_load_from_file(path, mp);
    if (!r->model) { error(err, err_size, "Model loading failed (memory, model, or driver)."); smart_close(r); return nullptr; }
    if (cancel(state)) { smart_close(r); return nullptr; }
    auto cp = p_llama_context_default_params(); cp.n_ctx = 8192; cp.n_batch = 256; cp.n_ubatch = 128;
    auto threads = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) / 2;
    cp.n_threads = cp.n_threads_batch = threads > 0 ? (int)threads : 1;
    cp.offload_kqv = cp.op_offload = gpu != 0;
    r->context = p_llama_init_from_model(r->model, cp);
    if (!r->context) { error(err, err_size, "Cannot allocate inference context."); smart_close(r); return nullptr; }
    return r;
}
extern "C" __declspec(dllexport) int __cdecl smart_token_count(runtime* r, const char* prompt) {
    return -p_llama_tokenize(p_llama_model_get_vocab(r->model), prompt, lstrlenA(prompt), nullptr, 0, true, true);
}
extern "C" __declspec(dllexport) int __cdecl smart_generate_v2(runtime* r, const char* prompt, int max_tokens, int protocol,
    const char* grammar, cancel_fn cancel, void* state, char* output, int capacity, char* err, int err_size) {
    const char* failure = nullptr; int length = 0;
    auto vocab = p_llama_model_get_vocab(r->model);
    int count = -p_llama_tokenize(vocab, prompt, lstrlenA(prompt), nullptr, 0, true, true);
    if (count <= 0 || count + max_tokens > 8192) { error(err, err_size, "Input exceeds the model context budget."); return -1; }
    auto tokens = (llama_token*)alloc(count * sizeof(llama_token));
    if (!tokens) { error(err, err_size, "Out of memory."); return -1; }
    p_llama_tokenize(vocab, prompt, lstrlenA(prompt), tokens, count, true, true);
    p_llama_memory_clear(p_llama_get_memory(r->context), true);
    cancellation c{cancel, state}; p_llama_set_abort_callback(r->context, aborted, &c);
    auto sampler = p_llama_sampler_chain_init(p_llama_sampler_chain_default_params());
    if (grammar && *grammar) {
        auto constrained = p_llama_sampler_init_grammar(vocab, grammar, "root");
        if (!constrained) failure = "Invalid output grammar.";
        else p_llama_sampler_chain_add(sampler, constrained);
    }
    p_llama_sampler_chain_add(sampler, p_llama_sampler_init_greedy());
    for (int i = 0; !failure && i < count; i += 128) {
        if (cancel(state)) failure = "Cancelled.";
        else if (p_llama_decode(r->context, p_llama_batch_get_one(tokens+i, smaller(128, count-i)))) failure = "Prompt evaluation failed.";
    }
    for (int i = 0; !failure && i < max_tokens; ++i) {
        if (cancel(state)) { failure = "Cancelled."; break; }
        auto token = p_llama_sampler_sample(sampler, r->context, -1);
        if (p_llama_vocab_is_eog(vocab, token)) break;
        char piece[512]; int bytes = p_llama_token_to_piece(vocab, token, piece, sizeof(piece), 0, false);
        if (bytes < 0 || length+bytes >= capacity) { failure = "Output buffer exceeded."; break; }
        memcpy(output+length, piece, bytes); length += bytes;
        if (p_llama_decode(r->context, p_llama_batch_get_one(&token, 1))) failure = "Generation failed.";
    }
    p_llama_set_abort_callback(r->context, nullptr, nullptr);
    p_llama_sampler_free(sampler); release(tokens);
    if (failure) { error(err, err_size, failure); return -1; }
    output[length] = 0; return length;
}
