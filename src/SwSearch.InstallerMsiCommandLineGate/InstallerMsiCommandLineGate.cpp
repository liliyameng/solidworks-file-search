#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <objbase.h>
#include <msiquery.h>

#include <string>
#include <vector>

#pragma comment(lib, "msi.lib")
#pragma comment(lib, "ole32.lib")

namespace {
constexpr wchar_t kActionProperty[] = L"SwSearchPrivilegedHandle";
constexpr wchar_t kRootProperty[] = L"SWSEARCHTRUSTEDROOTPOLICY";
constexpr wchar_t kActionNameProperty[] = L"SWSEARCHINSTALLACTION";
constexpr wchar_t kDeadlineProperty[] = L"SWSEARCHDEADLINETICK";
constexpr wchar_t kRootPolicy[] = L"registered-component-final-program-files";
constexpr wchar_t kFixedAction[] = L"install";
constexpr wchar_t kFixedDeadline[] = L"123456789";
constexpr wchar_t kExpectedRawCadTemplate[] =
    L"v=2;user=0;sid=;session=;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};"
    L"nonce=;root=[SWSEARCHTRUSTEDROOTPOLICY];action=[SWSEARCHINSTALLACTION];"
    L"deadline=[SWSEARCHDEADLINETICK]";
constexpr wchar_t kExpectedCad[] =
    L"v=2;user=0;sid=;session=;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};"
    L"nonce=;root=registered-component-final-program-files;action=install;deadline=123456789";
constexpr wchar_t kExpectedDeferredTarget[] =
    L"--privileged-handle --custom-action-data \"[SwSearchPrivilegedHandle]\"";
constexpr wchar_t kExpectedFormattedCommand[] =
    L"--privileged-handle --custom-action-data \""
    L"v=2;user=0;sid=;session=;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};"
    L"nonce=;root=registered-component-final-program-files;action=install;deadline=123456789\"";
constexpr int kSetPropertyType = 8243;
constexpr int kDeferredType = 11330;
constexpr DWORD kProbeTimeoutMilliseconds = 5000;

class ComApartment final {
public:
    ComApartment() noexcept : result_(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)) {}
    ~ComApartment() noexcept { if (SUCCEEDED(result_)) CoUninitialize(); }
    ComApartment(const ComApartment&) = delete;
    ComApartment& operator=(const ComApartment&) = delete;
    bool initialized() const noexcept { return SUCCEEDED(result_); }
private:
    HRESULT result_;
};

class MsiHandle final {
public:
    explicit MsiHandle(MSIHANDLE value = 0) noexcept : value_(value) {}
    ~MsiHandle() noexcept { if (value_ != 0) MsiCloseHandle(value_); }
    MsiHandle(const MsiHandle&) = delete;
    MsiHandle& operator=(const MsiHandle&) = delete;
    MSIHANDLE get() const noexcept { return value_; }
private:
    MSIHANDLE value_;
};

class WinHandle final {
public:
    explicit WinHandle(HANDLE value = nullptr) noexcept : value_(value) {}
    ~WinHandle() noexcept { close(); }
    WinHandle(const WinHandle&) = delete;
    WinHandle& operator=(const WinHandle&) = delete;
    HANDLE get() const noexcept { return value_; }
    bool close() noexcept {
        if (value_ == nullptr || value_ == INVALID_HANDLE_VALUE) return true;
        const HANDLE value = value_;
        if (!CloseHandle(value)) return false;
        value_ = nullptr;
        return true;
    }
private:
    HANDLE value_;
};

bool ReadRecordString(MSIHANDLE record, unsigned field, std::wstring& value) {
    DWORD characters = 0;
    wchar_t probe = L'\0';
    const auto first = MsiRecordGetStringW(record, field, &probe, &characters);
    if (first != ERROR_MORE_DATA && first != ERROR_SUCCESS) return false;
    std::vector<wchar_t> buffer(static_cast<size_t>(characters) + 1, L'\0');
    DWORD capacity = static_cast<DWORD>(buffer.size());
    if (MsiRecordGetStringW(record, field, buffer.data(), &capacity) != ERROR_SUCCESS) return false;
    value.assign(buffer.data(), capacity);
    return true;
}

bool ReadProperty(MSIHANDLE package, const wchar_t* name, std::wstring& value) {
    DWORD characters = 0;
    wchar_t probe = L'\0';
    const auto first = MsiGetPropertyW(package, name, &probe, &characters);
    if (first != ERROR_MORE_DATA && first != ERROR_SUCCESS) return false;
    std::vector<wchar_t> buffer(static_cast<size_t>(characters) + 1, L'\0');
    DWORD capacity = static_cast<DWORD>(buffer.size());
    if (MsiGetPropertyW(package, name, buffer.data(), &capacity) != ERROR_SUCCESS) return false;
    value.assign(buffer.data(), capacity);
    return true;
}

bool QueryCustomAction(
    MSIHANDLE package,
    const wchar_t* action,
    int& type,
    std::wstring& source,
    std::wstring& target) {
    MsiHandle database(MsiGetActiveDatabase(package));
    if (database.get() == 0) return false;
    const std::wstring sql =
        L"SELECT `Type`,`Source`,`Target` FROM `CustomAction` WHERE `Action`='" +
        std::wstring(action) + L"'";
    MSIHANDLE rawView = 0;
    if (MsiDatabaseOpenViewW(database.get(), sql.c_str(), &rawView) != ERROR_SUCCESS) return false;
    MsiHandle view(rawView);
    if (MsiViewExecute(view.get(), 0) != ERROR_SUCCESS) return false;
    MSIHANDLE rawRecord = 0;
    if (MsiViewFetch(view.get(), &rawRecord) != ERROR_SUCCESS) return false;
    MsiHandle record(rawRecord);
    type = MsiRecordGetInteger(record.get(), 1);
    if (!ReadRecordString(record.get(), 2, source) ||
        !ReadRecordString(record.get(), 3, target)) return false;
    MSIHANDLE extra = 0;
    const auto extraResult = MsiViewFetch(view.get(), &extra);
    if (extra != 0) MsiCloseHandle(extra);
    return extraResult == ERROR_NO_MORE_ITEMS;
}

bool FormatWithInstaller(MSIHANDLE package, const std::wstring& text, std::wstring& value) {
    MsiHandle record(MsiCreateRecord(0));
    if (record.get() == 0 || MsiRecordSetStringW(record.get(), 0, text.c_str()) != ERROR_SUCCESS) return false;
    DWORD characters = 0;
    wchar_t probe = L'\0';
    const auto first = MsiFormatRecordW(package, record.get(), &probe, &characters);
    if (first != ERROR_MORE_DATA && first != ERROR_SUCCESS) return false;
    std::vector<wchar_t> buffer(static_cast<size_t>(characters) + 1, L'\0');
    DWORD capacity = static_cast<DWORD>(buffer.size());
    if (MsiFormatRecordW(package, record.get(), buffer.data(), &capacity) != ERROR_SUCCESS) return false;
    value.assign(buffer.data(), capacity);
    return true;
}

bool IsExactMachineOnlyCad(const wchar_t* value) noexcept {
    return value != nullptr && wcscmp(value, kExpectedCad) == 0;
}

bool IsExactRawCadTemplate(const wchar_t* value) noexcept {
    return value != nullptr && wcscmp(value, kExpectedRawCadTemplate) == 0;
}

bool IsExactDeferredTarget(const wchar_t* value) noexcept {
    return value != nullptr && wcscmp(value, kExpectedDeferredTarget) == 0;
}

bool IsExactActionProperty(const wchar_t* value) noexcept {
    return value != nullptr && wcscmp(value, kActionProperty) == 0;
}

bool OracleRejectsUnsafeFixtures() noexcept {
    constexpr wchar_t hardCodedType51[] =
        L"v=2;user=0;sid=;session=;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};"
        L"nonce=;root=registered-component-final-program-files;action=install;deadline=123456789";
    constexpr wchar_t customActionDataTarget[] =
        L"--privileged-handle --custom-action-data \"[CustomActionData]\"";
    constexpr wchar_t wrongActionProperty[] = L"SwSearchPrivilegedHandleWrong";
    constexpr wchar_t sidInjectedType51[] =
        L"v=2;user=0;sid=S-1-5-18;session=;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};"
        L"nonce=;root=[SWSEARCHTRUSTEDROOTPOLICY];action=[SWSEARCHINSTALLACTION];"
        L"deadline=[SWSEARCHDEADLINETICK]";
    constexpr wchar_t sessionInjectedType51[] =
        L"v=2;user=0;sid=;session=1;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};"
        L"nonce=;root=[SWSEARCHTRUSTEDROOTPOLICY];action=[SWSEARCHINSTALLACTION];"
        L"deadline=[SWSEARCHDEADLINETICK]";
    constexpr wchar_t nonceInjectedType51[] =
        L"v=2;user=0;sid=;session=;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};"
        L"nonce=87D8EC95-3AD7-4806-885B-264101E6D332;root=[SWSEARCHTRUSTEDROOTPOLICY];"
        L"action=[SWSEARCHINSTALLACTION];deadline=[SWSEARCHDEADLINETICK]";
    return IsExactRawCadTemplate(kExpectedRawCadTemplate) &&
        IsExactDeferredTarget(kExpectedDeferredTarget) &&
        IsExactActionProperty(kActionProperty) &&
        !IsExactRawCadTemplate(hardCodedType51) &&
        !IsExactDeferredTarget(customActionDataTarget) &&
        !IsExactActionProperty(wrongActionProperty) &&
        !IsExactRawCadTemplate(sidInjectedType51) &&
        !IsExactRawCadTemplate(sessionInjectedType51) &&
        !IsExactRawCadTemplate(nonceInjectedType51);
}

bool TerminateAndReapSelfCreatedProcess(HANDLE process) noexcept {
    const BOOL terminated = TerminateProcess(process, 95);
    const DWORD secondWait = WaitForSingleObject(process, kProbeTimeoutMilliseconds);
    return terminated != FALSE && secondWait == WAIT_OBJECT_0;
}

bool RunProbe(const std::wstring& executable, const std::wstring& arguments, DWORD expectedExitCode) {
    WinHandle job(CreateJobObjectW(nullptr, nullptr));
    if (job.get() == nullptr) return false;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobLimits{};
    jobLimits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(
            job.get(), JobObjectExtendedLimitInformation, &jobLimits, sizeof(jobLimits))) return false;

    std::wstring commandLine = L"\"" + executable + L"\" " + arguments;
    std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
    mutableCommand.push_back(L'\0');
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION processInfo{};
    if (!CreateProcessW(
            executable.c_str(),
            mutableCommand.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_NO_WINDOW | CREATE_SUSPENDED,
            nullptr,
            nullptr,
            &startup,
            &processInfo)) return false;
    WinHandle process(processInfo.hProcess);
    WinHandle thread(processInfo.hThread);
    if (!AssignProcessToJobObject(job.get(), process.get())) {
        if (!TerminateAndReapSelfCreatedProcess(process.get())) return false;
        return false;
    }
    if (ResumeThread(thread.get()) == static_cast<DWORD>(-1)) {
        if (!TerminateAndReapSelfCreatedProcess(process.get())) return false;
        return false;
    }
    const auto wait = WaitForSingleObject(process.get(), kProbeTimeoutMilliseconds);
    if (wait != WAIT_OBJECT_0) {
        if (!job.close()) {
            if (!TerminateAndReapSelfCreatedProcess(process.get())) return false;
            return false;
        }
        const DWORD secondWait = WaitForSingleObject(process.get(), kProbeTimeoutMilliseconds);
        if (secondWait != WAIT_OBJECT_0) return false;
        return false;
    }
    DWORD exitCode = 0;
    return GetExitCodeProcess(process.get(), &exitCode) && exitCode == expectedExitCode;
}

int VerifyFinalMsi(const wchar_t* msiPath) {
    MSIHANDLE rawPackage = 0;
    if (MsiOpenPackageExW(msiPath, 1, &rawPackage) != ERROR_SUCCESS) return 65;
    MsiHandle package(rawPackage);

    int setType = 0;
    std::wstring setSource;
    std::wstring setTarget;
    if (!QueryCustomAction(
            package.get(), L"SetSwSearchPrivilegedHandleData", setType, setSource, setTarget) ||
        setType != kSetPropertyType || !IsExactActionProperty(setSource.c_str()) ||
        !IsExactRawCadTemplate(setTarget.c_str())) return 66;
    std::wstring initialActionProperty;
    if (!ReadProperty(package.get(), kActionProperty, initialActionProperty) ||
        !initialActionProperty.empty()) return 74;
    if (MsiSetPropertyW(package.get(), kRootProperty, kRootPolicy) != ERROR_SUCCESS ||
        MsiSetPropertyW(package.get(), kActionNameProperty, kFixedAction) != ERROR_SUCCESS ||
        MsiSetPropertyW(package.get(), kDeadlineProperty, kFixedDeadline) != ERROR_SUCCESS) return 67;

    std::wstring cad;
    if (!FormatWithInstaller(package.get(), setTarget, cad) || cad != kExpectedCad ||
        MsiSetPropertyW(package.get(), kActionProperty, cad.c_str()) != ERROR_SUCCESS) return 68;

    int deferredType = 0;
    std::wstring deferredSource;
    std::wstring deferredTarget;
    if (!QueryCustomAction(
            package.get(), L"SwSearchPrivilegedHandle", deferredType, deferredSource, deferredTarget) ||
        deferredType != kDeferredType ||
        deferredSource != L"SwSearchInstallerCoordinator" ||
        !IsExactDeferredTarget(deferredTarget.c_str())) return 69;

    std::wstring command;
    if (!FormatWithInstaller(package.get(), deferredTarget, command) ||
        command != kExpectedFormattedCommand) return 70;

    std::vector<wchar_t> module(32768, L'\0');
    const auto moduleLength = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    if (moduleLength == 0 || moduleLength >= module.size()) return 71;
    const std::wstring executable(module.data(), moduleLength);
    if (!RunProbe(executable, command, 0)) return 72;

    const std::wstring sidInjection =
        L"--privileged-handle --custom-action-data \"v=2;user=0;sid=S-1-5-18;session=;"
        L"upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};nonce=;root="
        L"registered-component-final-program-files;action=install;deadline=123456789\"";
    const std::wstring sessionInjection =
        L"--privileged-handle --custom-action-data \"v=2;user=0;sid=;session=1;"
        L"upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};nonce=;root="
        L"registered-component-final-program-files;action=install;deadline=123456789\"";
    const std::wstring nonceInjection =
        L"--privileged-handle --custom-action-data \"v=2;user=0;sid=;session=;"
        L"upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};nonce=87D8EC95-3AD7-4806-885B-264101E6D332;root="
        L"registered-component-final-program-files;action=install;deadline=123456789\"";
    if (!RunProbe(executable, sidInjection, 91) ||
        !RunProbe(executable, sessionInjection, 91) ||
        !RunProbe(executable, nonceInjection, 91)) return 73;
    return 0;
}
} // namespace

int wmain(int argc, wchar_t** argv) noexcept {
    ComApartment apartment;
    if (!apartment.initialized()) return 92;
    if (!OracleRejectsUnsafeFixtures()) return 93;
    if (argc == 4 &&
        wcscmp(argv[1], L"--privileged-handle") == 0 &&
        wcscmp(argv[2], L"--custom-action-data") == 0) {
        return IsExactMachineOnlyCad(argv[3]) ? 0 : 91;
    }
    if (argc != 3 || wcscmp(argv[1], L"--verify-msi") != 0) return 64;
    try {
        return VerifyFinalMsi(argv[2]);
    } catch (...) {
        return 90;
    }
}
