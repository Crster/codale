// Codale Explorer context menu handler.
//
// This DLL is loaded in-process by explorer.exe, which is why it is C++ rather than
// managed: Explorer will not host the CLR for a context menu handler, and the Windows 11
// *modern* (top-level) context menu only accepts a packaged COM server.
//
// It is deliberately tiny. Its entire job is to work out which folder was clicked and
// hand it to the app as codale://open?path=... Every decision that matters - which
// window owns a project, whether to focus an existing one - lives in the managed app,
// so a bug there never destabilises Explorer.

#include <windows.h>
// WIN32_LEAN_AND_MEAN drops shellapi.h, which declares ShellExecuteEx.
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <string>

using namespace Microsoft::WRL;

// {0A5761EB-EF52-4D9B-8ADF-4D5B5334766F}
// Must match the Class Id declared in Package.appxmanifest.
static const CLSID CLSID_CodaleOpenCommand =
{ 0x0a5761eb, 0xef52, 0x4d9b, { 0x8a, 0xdf, 0x4d, 0x5b, 0x53, 0x34, 0x76, 0x6f } };

static HMODULE g_module = nullptr;
static LONG g_objectCount = 0;

namespace
{
    // Percent-encodes everything outside the RFC 3986 unreserved set, so drive colons,
    // backslashes and spaces survive the trip through the protocol handler intact.
    std::wstring EscapeDataString(const std::wstring& value)
    {
        static const wchar_t* hex = L"0123456789ABCDEF";

        // Percent-encoding works on UTF-8 bytes, so convert the whole string in one call:
        // converting a UTF-16 unit at a time splits surrogate pairs and corrupts any
        // character outside the BMP.
        std::string utf8;
        if (!value.empty())
        {
            const int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
            if (size > 0)
            {
                utf8.resize(size);
                WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), &utf8[0], size, nullptr, nullptr);
            }
        }

        std::wstring escaped;
        escaped.reserve(utf8.size() * 3);

        for (const char raw : utf8)
        {
            const unsigned char b = static_cast<unsigned char>(raw);
            const bool unreserved =
                (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                (b >= '0' && b <= '9') ||
                b == '-' || b == '_' || b == '.' || b == '~';

            if (unreserved)
            {
                escaped.push_back(static_cast<wchar_t>(b));
            }
            else
            {
                escaped.push_back(L'%');
                escaped.push_back(hex[(b >> 4) & 0xF]);
                escaped.push_back(hex[b & 0xF]);
            }
        }

        return escaped;
    }

    bool IsDirectory(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES &&
               (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    // Resolves the folder for a normal selection.
    HRESULT PathFromSelection(IShellItemArray* selection, std::wstring& path)
    {
        if (selection == nullptr)
        {
            return E_INVALIDARG;
        }

        DWORD count = 0;
        HRESULT hr = selection->GetCount(&count);
        if (FAILED(hr) || count == 0)
        {
            return FAILED(hr) ? hr : E_INVALIDARG;
        }

        ComPtr<IShellItem> item;
        hr = selection->GetItemAt(0, &item);
        if (FAILED(hr))
        {
            return hr;
        }

        PWSTR raw = nullptr;
        hr = item->GetDisplayName(SIGDN_FILESYSPATH, &raw);
        if (FAILED(hr))
        {
            return hr;
        }

        path.assign(raw);
        CoTaskMemFree(raw);
        return S_OK;
    }

    // Resolves the folder when the user right-clicked empty space inside a folder
    // ("Directory\Background"); there is no selection, so the folder comes from the site.
    HRESULT PathFromSite(IUnknown* site, std::wstring& path)
    {
        if (site == nullptr)
        {
            return E_FAIL;
        }

        ComPtr<IServiceProvider> services;
        HRESULT hr = site->QueryInterface(IID_PPV_ARGS(&services));
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<IFolderView> folderView;
        hr = services->QueryService(SID_SFolderView, IID_PPV_ARGS(&folderView));
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<IShellItem> folder;
        hr = folderView->GetFolder(IID_PPV_ARGS(&folder));
        if (FAILED(hr))
        {
            return hr;
        }

        PWSTR raw = nullptr;
        hr = folder->GetDisplayName(SIGDN_FILESYSPATH, &raw);
        if (FAILED(hr))
        {
            return hr;
        }

        path.assign(raw);
        CoTaskMemFree(raw);
        return S_OK;
    }
}

class CodaleOpenCommand :
    public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>
{
public:
    CodaleOpenCommand() { InterlockedIncrement(&g_objectCount); }
    ~CodaleOpenCommand() { InterlockedDecrement(&g_objectCount); }

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
    {
        return SHStrDupW(L"Open in Codale", title);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        // Resource path relative to this DLL, which lives beside the app in the package.
        wchar_t module[MAX_PATH] = {};
        if (GetModuleFileNameW(g_module, module, ARRAYSIZE(module)) == 0)
        {
            *icon = nullptr;
            return E_FAIL;
        }

        std::wstring iconPath(module);
        const size_t slash = iconPath.find_last_of(L'\\');
        if (slash != std::wstring::npos)
        {
            iconPath.erase(slash + 1);
        }
        iconPath += L"Assets\\AppIcon.ico";

        return SHStrDupW(iconPath.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    {
        *name = GUID_NULL;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* selection, BOOL, EXPCMDSTATE* state) override
    {
        // Offer the verb for folders and drives only, never for files.
        std::wstring path;
        if (SUCCEEDED(PathFromSelection(selection, path)) && IsDirectory(path))
        {
            *state = ECS_ENABLED;
            return S_OK;
        }

        if (selection == nullptr && SUCCEEDED(PathFromSite(m_site.Get(), path)))
        {
            *state = ECS_ENABLED;
            return S_OK;
        }

        *state = ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) noexcept override
    {
        std::wstring path;

        if (FAILED(PathFromSelection(selection, path)) || !IsDirectory(path))
        {
            if (FAILED(PathFromSite(m_site.Get(), path)))
            {
                return E_FAIL;
            }
        }

        const std::wstring uri = L"codale://open?path=" + EscapeDataString(path);

        // Async: the URI outlives this call, and NOASYNC would block the shell thread on launch.
        SHELLEXECUTEINFOW info = { sizeof(info) };
        info.fMask = SEE_MASK_FLAG_NO_UI;
        info.lpVerb = L"open";
        info.lpFile = uri.c_str();
        info.nShow = SW_SHOWNORMAL;

        return ShellExecuteExW(&info) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }

    // IObjectWithSite - Explorer hands us the folder view this menu belongs to.
    IFACEMETHODIMP SetSite(IUnknown* site) noexcept override
    {
        m_site = site;
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID riid, void** site) noexcept override
    {
        return m_site.CopyTo(riid, site);
    }

private:
    ComPtr<IUnknown> m_site;
};

class CodaleClassFactory : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IClassFactory>
{
public:
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** result) noexcept override
    {
        *result = nullptr;
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        auto command = Make<CodaleOpenCommand>();
        if (!command)
        {
            return E_OUTOFMEMORY;
        }

        return command.CopyTo(riid, result);
    }

    IFACEMETHODIMP LockServer(BOOL lock) noexcept override
    {
        if (lock)
        {
            InterlockedIncrement(&g_objectCount);
        }
        else
        {
            InterlockedDecrement(&g_objectCount);
        }

        return S_OK;
    }
};

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** result)
{
    *result = nullptr;

    if (!IsEqualCLSID(clsid, CLSID_CodaleOpenCommand))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto factory = Make<CodaleClassFactory>();
    if (!factory)
    {
        return E_OUTOFMEMORY;
    }

    return factory.CopyTo(riid, result);
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}
