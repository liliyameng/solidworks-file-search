#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <shlguid.h>
#include <new>
#include <string>
#include <vector>

const CLSID CLSID_SwSearchExplorerCommand =
{ 0x5c2e54ce, 0x4ef3, 0x4cd3, { 0xa4, 0xa1, 0x6d, 0xe1, 0x34, 0xab, 0x78, 0x29 } };
const CLSID CLSID_SwSearchFilePropertiesCommand =
{ 0x7a784fd1, 0x89a1, 0x4b68, { 0x97, 0xd0, 0x54, 0x5e, 0xe7, 0x0d, 0x3a, 0x25 } };

static LONG g_objects = 0;
static LONG g_locks = 0;
static HMODULE g_module = nullptr;

static HRESULT GetInstallDirectory(std::wstring& directory)
{
    wchar_t value[32768]{};
    DWORD bytes = sizeof(value);
    const LONG result = RegGetValueW(
        HKEY_LOCAL_MACHINE, L"SOFTWARE\\SwSearch\\Installer", L"InstallDirectory",
        RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, value, &bytes);
    if (result != ERROR_SUCCESS || value[0] == L'\0')
        return HRESULT_FROM_WIN32(result == ERROR_SUCCESS ? ERROR_PATH_NOT_FOUND : result);
    directory.assign(value);
    while (!directory.empty() && (directory.back() == L'\\' || directory.back() == L'/')) directory.pop_back();
    if (directory.empty() || directory.find(L'"') != std::wstring::npos)
        return HRESULT_FROM_WIN32(ERROR_INVALID_NAME);
    return S_OK;
}

static HRESULT GetItemPath(IShellItemArray* items, IUnknown* site, PWSTR* path)
{
    if (!path) return E_INVALIDARG;
    *path = nullptr;
    if (items)
    {
        DWORD count = 0;
        HRESULT hr = items->GetCount(&count);
        if (FAILED(hr)) return hr;
        if (count > 0)
        {
            IShellItem* item = nullptr;
            hr = items->GetItemAt(0, &item);
            if (SUCCEEDED(hr)) hr = item->GetDisplayName(SIGDN_FILESYSPATH, path);
            if (item) item->Release();
            return hr;
        }
    }

    IServiceProvider* services = nullptr;
    IFolderView* view = nullptr;
    IShellItem* folder = nullptr;
    HRESULT hr = site ? site->QueryInterface(IID_PPV_ARGS(&services)) : E_NOINTERFACE;
    if (SUCCEEDED(hr)) hr = services->QueryService(SID_SFolderView, IID_PPV_ARGS(&view));
    if (SUCCEEDED(hr)) hr = view->GetFolder(IID_PPV_ARGS(&folder));
    if (SUCCEEDED(hr)) hr = folder->GetDisplayName(SIGDN_FILESYSPATH, path);
    if (folder) folder->Release();
    if (view) view->Release();
    if (services) services->Release();
    return hr;
}

static std::wstring QuoteCommandLineArgument(PCWSTR value)
{
    std::wstring quoted(L"\"");
    size_t backslashes = 0;
    for (const wchar_t* cursor = value; *cursor != L'\0'; ++cursor)
    {
        if (*cursor == L'\\')
        {
            ++backslashes;
            continue;
        }
        if (*cursor == L'\"')
        {
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted.push_back(L'\"');
            backslashes = 0;
            continue;
        }
        quoted.append(backslashes, L'\\');
        backslashes = 0;
        quoted.push_back(*cursor);
    }
    quoted.append(backslashes * 2, L'\\');
    quoted.push_back(L'\"');
    return quoted;
}

static HRESULT LaunchSearch(PCWSTR folder)
{
    if (!folder || folder[0] == L'\0') return E_INVALIDARG;
    const DWORD attributes = GetFileAttributesW(folder);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        return HRESULT_FROM_WIN32(ERROR_DIRECTORY);

    std::wstring application;
    HRESULT installResult = GetInstallDirectory(application);
    if (FAILED(installResult)) return installResult;
    std::wstring workingDirectory(application);
    application += L"\\SwSearch.App.exe";
    const DWORD appAttributes = GetFileAttributesW(application.c_str());
    if (appAttributes == INVALID_FILE_ATTRIBUTES || (appAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    std::wstring commandLine = QuoteCommandLineArgument(application.c_str());
    commandLine += L" --scope-folder ";
    commandLine += QuoteCommandLineArgument(folder);
    std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
    mutableCommand.push_back(L'\0');

    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(
            application.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE,
            CREATE_UNICODE_ENVIRONMENT, nullptr, workingDirectory.c_str(), &startup, &process))
        return HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

static bool IsSolidWorksDocument(PCWSTR path)
{
    const wchar_t* extension = PathFindExtensionW(path);
    return extension &&
        (_wcsicmp(extension, L".sldprt") == 0 || _wcsicmp(extension, L".sldasm") == 0);
}

static bool IsCommandEnabled(bool fileCommand)
{
    DWORD value = 0;
    DWORD bytes = sizeof(value);
    const wchar_t* name = fileCommand ? L"FilePropertiesEnabled" : L"FolderSearchEnabled";
    return RegGetValueW(HKEY_CURRENT_USER, L"Software\\SwSearch\\ModernContextMenu", name,
        RRF_RT_REG_DWORD, nullptr, &value, &bytes) == ERROR_SUCCESS && value != 0;
}

static HRESULT LaunchProperties(PCWSTR file)
{
    if (!file || !IsSolidWorksDocument(file)) return E_INVALIDARG;
    const DWORD attributes = GetFileAttributesW(file);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    std::wstring directory;
    HRESULT hr = GetInstallDirectory(directory);
    if (FAILED(hr)) return hr;
    std::wstring application = directory + L"\\SwSearch.PropertyViewer.exe";
    std::wstring commandLine = QuoteCommandLineArgument(application.c_str()) + L" " + QuoteCommandLineArgument(file);
    std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
    mutableCommand.push_back(L'\0');
    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(application.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE,
            CREATE_UNICODE_ENVIRONMENT, nullptr, directory.c_str(), &startup, &process))
        return HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

class ExplorerCommand final : public IExplorerCommand, public IObjectWithSite
{
    LONG references_ = 1;
    IUnknown* site_ = nullptr;
    bool fileCommand_ = false;
public:
    explicit ExplorerCommand(bool fileCommand) : fileCommand_(fileCommand) { InterlockedIncrement(&g_objects); }
    ~ExplorerCommand() { if (site_) site_->Release(); InterlockedDecrement(&g_objects); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid == IID_IUnknown || iid == IID_IExplorerCommand) *value = static_cast<IExplorerCommand*>(this);
        else if (iid == IID_IObjectWithSite) *value = static_cast<IObjectWithSite*>(this);
        if (!*value) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&references_); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG value = InterlockedDecrement(&references_);
        if (!value) delete this;
        return value;
    }
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* title) override
    {
        return SHStrDupW(fileCommand_ ? L"\u67E5\u770B SOLIDWORKS \u5C5E\u6027" : L"\u7528 SOLIDWORKS \u6587\u4EF6\u68C0\u7D22\u641C\u7D22", title);
    }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        if (!icon) return E_POINTER;
        *icon = nullptr;
        std::wstring value;
        HRESULT hr = GetInstallDirectory(value);
        if (FAILED(hr)) return hr;
        value += L"\\SwSearch.ico";
        const DWORD attributes = GetFileAttributesW(value.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        return SHStrDupW(value.c_str(), icon);
    }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        return SHStrDupW(fileCommand_ ? L"\u5FEB\u901F\u67E5\u770B SOLIDWORKS \u6587\u4EF6\u5C5E\u6027" : L"\u4EC5\u641C\u7D22\u5F53\u524D\u76EE\u5F55\u53CA\u5176\u5168\u90E8\u5B50\u76EE\u5F55", tip);
    }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        if (!guid) return E_POINTER;
        *guid = fileCommand_ ? CLSID_SwSearchFilePropertiesCommand : CLSID_SwSearchExplorerCommand;
        return S_OK;
    }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        if (!state) return E_POINTER;
        *state = IsCommandEnabled(fileCommand_) ? ECS_ENABLED : ECS_HIDDEN;
        if (*state == ECS_ENABLED && fileCommand_)
        {
            PWSTR path = nullptr;
            if (FAILED(GetItemPath(items, site_, &path)) || !path || !IsSolidWorksDocument(path)) *state = ECS_HIDDEN;
            CoTaskMemFree(path);
        }
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        PWSTR path = nullptr;
        HRESULT hr = GetItemPath(items, site_, &path);
        if (SUCCEEDED(hr)) hr = fileCommand_ ? LaunchProperties(path) : LaunchSearch(path);
        CoTaskMemFree(path);
        return hr;
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (!flags) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (commands) *commands = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        if (site) site->AddRef();
        if (site_) site_->Release();
        site_ = site;
        return S_OK;
    }
    IFACEMETHODIMP GetSite(REFIID iid, void** value) override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        return site_ ? site_->QueryInterface(iid, value) : E_FAIL;
    }
};

class Factory final : public IClassFactory
{
    LONG references_ = 1;
    bool fileCommand_ = false;
public:
    explicit Factory(bool fileCommand) : fileCommand_(fileCommand) {}
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override
    {
        if (!value) return E_POINTER;
        *value = (iid == IID_IUnknown || iid == IID_IClassFactory) ? static_cast<IClassFactory*>(this) : nullptr;
        if (!*value) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&references_); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG value = InterlockedDecrement(&references_);
        if (!value) delete this;
        return value;
    }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** value) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        ExplorerCommand* command = new (std::nothrow) ExplorerCommand(fileCommand_);
        if (!command) return E_OUTOFMEMORY;
        HRESULT hr = command->QueryInterface(iid, value);
        command->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        lock ? InterlockedIncrement(&g_locks) : InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** value)
{
    const bool fileCommand = IsEqualCLSID(clsid, CLSID_SwSearchFilePropertiesCommand);
    if (!fileCommand && !IsEqualCLSID(clsid, CLSID_SwSearchExplorerCommand)) return CLASS_E_CLASSNOTAVAILABLE;
    Factory* factory = new (std::nothrow) Factory(fileCommand);
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(iid, value);
    factory->Release();
    return hr;
}
STDAPI DllCanUnloadNow()
{
    return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE;
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
