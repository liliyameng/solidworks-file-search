#include <windows.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.Management.Deployment.h>
#include <string>
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Management::Deployment;

namespace {
constexpr wchar_t kPackageName[] = L"SwSearch.ModernContextMenu";
constexpr wchar_t kPackageFileName[] = L"SwSearch.ModernContextMenu.msix";
constexpr wchar_t kToggleKey[] = L"Software\\SwSearch\\ModernContextMenu";
constexpr ULONGLONG kDeploymentTimeoutMilliseconds = 3000;

template <typename TAsyncOperation>
bool WaitForDeployment(TAsyncOperation const& operation)
{
    const ULONGLONG deadline = GetTickCount64() + kDeploymentTimeoutMilliseconds;
    for (;;)
    {
        const auto status = operation.Status();
        if (status == AsyncStatus::Completed) return true;
        if (status == AsyncStatus::Canceled || status == AsyncStatus::Error) return false;
        if (GetTickCount64() >= deadline)
        {
            operation.Cancel();
            return false;
        }
        Sleep(25);
    }
}

std::wstring ModuleDirectory()
{
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
    path.resize(length);
    const auto slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos) throw winrt::hresult_error(E_UNEXPECTED);
    path.resize(slash);
    return path;
}

Uri FileUri(const std::wstring& path)
{
    DWORD length = 32768;
    std::wstring uri(length, L'\0');
    HRESULT result = UrlCreateFromPathW(path.c_str(), uri.data(), &length, 0);
    if (FAILED(result)) throw winrt::hresult_error(result);
    uri.resize(length);
    return Uri(uri);
}

int EnableLocal(PackageManager const& manager)
{
    const std::wstring directory = ModuleDirectory();
    const auto operation = manager.AddPackageAsync(
        FileUri(directory + L"\\" + kPackageFileName), nullptr, DeploymentOptions::None);
    if (!WaitForDeployment(operation)) return 9;
    DeploymentResult result = operation.GetResults();
    const int code = result.ExtendedErrorCode().value == 0 ? 0 : 2;
    if (code == 0)
    {
        HKEY key = nullptr;
        if (RegCreateKeyExW(HKEY_CURRENT_USER, kToggleKey, 0, nullptr, 0, KEY_QUERY_VALUE | KEY_SET_VALUE,
            nullptr, &key, nullptr) != ERROR_SUCCESS) return 7;
        for (const wchar_t* name : { L"FolderSearchEnabled", L"FilePropertiesEnabled" })
        {
            DWORD value = 1;
            DWORD bytes = sizeof(value);
            if (RegQueryValueExW(key, name, nullptr, nullptr, reinterpret_cast<BYTE*>(&value), &bytes) == ERROR_FILE_NOT_FOUND)
            {
                value = 1;
                if (RegSetValueExW(key, name, 0, REG_DWORD,
                    reinterpret_cast<const BYTE*>(&value), sizeof(value)) != ERROR_SUCCESS)
                {
                    RegCloseKey(key);
                    return 8;
                }
            }
        }
        RegCloseKey(key);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    }
    return code;
}

bool PackageRegistered(PackageManager const& manager)
{
    for (auto const& package : manager.FindPackagesForUser(L""))
        if (package.Id().Name() == kPackageName) return true;
    return false;
}

bool ReadToggle(const wchar_t* name)
{
    DWORD value = 0;
    DWORD bytes = sizeof(value);
    return RegGetValueW(HKEY_CURRENT_USER, kToggleKey, name, RRF_RT_REG_DWORD,
        nullptr, &value, &bytes) == ERROR_SUCCESS && value != 0;
}

int SetToggle(PackageManager const& manager, const wchar_t* name, bool enabled)
{
    if (enabled && !PackageRegistered(manager))
    {
        const int packageResult = EnableLocal(manager);
        if (packageResult != 0) return packageResult;
    }
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, kToggleKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        return 7;
    const DWORD value = enabled ? 1 : 0;
    const LONG result = RegSetValueExW(key, name, 0, REG_DWORD,
        reinterpret_cast<const BYTE*>(&value), sizeof(value));
    RegCloseKey(key);
    if (result != ERROR_SUCCESS) return 8;
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return 0;
}
}

int wmain(int argc, wchar_t** argv)
{
    try {
        init_apartment(apartment_type::multi_threaded);
        PackageManager manager;
        if (argc == 2 && std::wstring(argv[1]) == L"--enable-local") return EnableLocal(manager);
        if (argc == 2 && std::wstring(argv[1]) == L"--status-folder")
            return PackageRegistered(manager) && ReadToggle(L"FolderSearchEnabled") ? 0 : 1;
        if (argc == 2 && std::wstring(argv[1]) == L"--status-file")
            return PackageRegistered(manager) && ReadToggle(L"FilePropertiesEnabled") ? 0 : 1;
        if (argc == 3 && std::wstring(argv[1]) == L"--set-folder")
            return SetToggle(manager, L"FolderSearchEnabled", std::wstring(argv[2]) == L"1");
        if (argc == 3 && std::wstring(argv[1]) == L"--set-file")
            return SetToggle(manager, L"FilePropertiesEnabled", std::wstring(argv[2]) == L"1");
        if (argc == 2 && std::wstring(argv[1]) == L"--status")
        {
            for (auto const& package : manager.FindPackagesForUser(L"")) if (package.Id().Name() == kPackageName) return 0;
            return 1;
        }
        if (argc == 2 && std::wstring(argv[1]) == L"--disable")
        {
            bool changed = false;
            for (auto const& package : manager.FindPackagesForUser(L""))
            {
                if (package.Id().Name() == kPackageName)
                {
                    const auto operation = manager.RemovePackageAsync(package.Id().FullName());
                    if (!WaitForDeployment(operation)) return 9;
                    auto result = operation.GetResults();
                    if (result.ExtendedErrorCode().value != 0) return 3;
                    changed = true;
                }
            }
            if (changed) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
            RegDeleteTreeW(HKEY_CURRENT_USER, kToggleKey);
            return 0;
        }
        return 64;
    }
    catch (winrt::hresult_error const&) {
        return 5;
    }
    catch (...) {
        return 6;
    }
}
