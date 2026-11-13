#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <bcrypt.h>
#include <msiquery.h>
#include <sddl.h>
#include <wincrypt.h>

#include <array>
#include <climits>
#include <cwchar>
#include <string>
#include <vector>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "msi.lib")
#pragma comment(lib, "crypt32.lib")

namespace {
constexpr wchar_t kSidProperty[] = L"SWSEARCHTRUSTEDSID";
constexpr wchar_t kSessionProperty[] = L"SWSEARCHTRUSTEDSESSIONID";
constexpr wchar_t kNonceProperty[] = L"SWSEARCHSESSIONNONCE";
constexpr wchar_t kDeadlineProperty[] = L"SWSEARCHDEADLINETICK";
constexpr wchar_t kActionProperty[] = L"SWSEARCHINSTALLACTION";
constexpr wchar_t kRootPolicyProperty[] = L"SWSEARCHTRUSTEDROOTPOLICY";
constexpr wchar_t kReadyProperty[] = L"SWSEARCHCONTEXTREADY";
constexpr wchar_t kMachineReadyProperty[] = L"SWSEARCHMACHINECONTEXTREADY";
constexpr wchar_t kDotNetCore8Property[] = L"SWSEARCHDOTNETCORE8";
constexpr wchar_t kWindowsDesktop8Property[] = L"SWSEARCHWINDOWSDESKTOP8";
constexpr wchar_t kDotNet8ReadyProperty[] = L"SWSEARCHDOTNET8READY";
constexpr wchar_t kRegisteredRootPolicy[] = L"registered-component-final-program-files";
constexpr ULONGLONG kSessionDeadlineMilliseconds = 30000;

enum class FailureReason {
    InvalidMsiHandle,
    ThreadTokenAbsent,
    ThreadTokenOpenFailed,
    ThreadTokenTypeInvalid,
    ThreadImpersonationLevelInvalid,
    ProcessTokenOpenFailed,
    ProcessTokenTypeInvalid,
    TokenUserQueryFailed,
    TokenSessionQueryFailed,
    TokenSessionNonInteractive,
    ProcessTokenValidationFailed,
    TokenSidInvalid,
    TokenAppContainerRejected,
    ProcessTokenFallbackAccepted,
    NonceGenerationFailed,
    InstallActionInvalid,
    MachineContextWriteFailed,
    PrivatePropertyWriteFailed,
    UnexpectedFailure
};

const wchar_t* FailureCode(FailureReason reason) noexcept {
    switch (reason) {
    case FailureReason::InvalidMsiHandle: return L"INVALID_MSI_HANDLE";
    case FailureReason::ThreadTokenAbsent: return L"THREAD_TOKEN_ABSENT";
    case FailureReason::ThreadTokenOpenFailed: return L"THREAD_TOKEN_OPEN_FAILED";
    case FailureReason::ThreadTokenTypeInvalid: return L"THREAD_TOKEN_TYPE_INVALID";
    case FailureReason::ThreadImpersonationLevelInvalid: return L"THREAD_TOKEN_IMPERSONATION_LEVEL_INVALID";
    case FailureReason::ProcessTokenOpenFailed: return L"PROCESS_TOKEN_OPEN_FAILED";
    case FailureReason::ProcessTokenTypeInvalid: return L"PROCESS_TOKEN_TYPE_INVALID";
    case FailureReason::TokenUserQueryFailed: return L"TOKEN_USER_QUERY_FAILED";
    case FailureReason::TokenSessionQueryFailed: return L"TOKEN_SESSION_QUERY_FAILED";
    case FailureReason::TokenSessionNonInteractive: return L"TOKEN_SESSION_NONINTERACTIVE";
    case FailureReason::ProcessTokenValidationFailed: return L"PROCESS_TOKEN_VALIDATION_FAILED";
    case FailureReason::TokenSidInvalid: return L"TOKEN_SID_INVALID";
    case FailureReason::TokenAppContainerRejected: return L"TOKEN_APPCONTAINER_REJECTED";
    case FailureReason::ProcessTokenFallbackAccepted: return L"PROCESS_TOKEN_FALLBACK_ACCEPTED";
    case FailureReason::NonceGenerationFailed: return L"NONCE_GENERATION_FAILED";
    case FailureReason::InstallActionInvalid: return L"INSTALL_ACTION_INVALID";
    case FailureReason::MachineContextWriteFailed: return L"MACHINE_CONTEXT_WRITE_FAILED";
    case FailureReason::PrivatePropertyWriteFailed: return L"PRIVATE_PROPERTY_WRITE_FAILED";
    default: return L"UNEXPECTED_FAILURE";
    }
}

void LogReason(MSIHANDLE session, FailureReason reason, bool fatal) noexcept {
    if (session == 0) {
        return;
    }

    MSIHANDLE record = MsiCreateRecord(1);
    if (record == 0) {
        return;
    }

    wchar_t message[128]{};
    if (swprintf_s(
            message,
            fatal
                ? L"SwSearch installer context capture failed: %ls"
                : L"SwSearch installer context diagnostic: %ls",
            FailureCode(reason)) <= 0) {
        MsiCloseHandle(record);
        return;
    }
    if (MsiRecordSetStringW(record, 0, message) == ERROR_SUCCESS) {
        MsiProcessMessage(session, fatal ? INSTALLMESSAGE_ERROR : INSTALLMESSAGE_INFO, record);
    }
    MsiCloseHandle(record);
    SecureZeroMemory(message, sizeof(message));
}

class UniqueHandle final {
public:
    explicit UniqueHandle(HANDLE value = nullptr) noexcept : value_(value) {}
    ~UniqueHandle() noexcept {
        if (value_ != nullptr && value_ != INVALID_HANDLE_VALUE) {
            CloseHandle(value_);
        }
    }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    HANDLE get() const noexcept { return value_; }

private:
    HANDLE value_;
};

class UniqueFindHandle final {
public:
    explicit UniqueFindHandle(HANDLE value = INVALID_HANDLE_VALUE) noexcept : value_(value) {}
    ~UniqueFindHandle() noexcept {
        if (value_ != INVALID_HANDLE_VALUE) FindClose(value_);
    }
    UniqueFindHandle(const UniqueFindHandle&) = delete;
    UniqueFindHandle& operator=(const UniqueFindHandle&) = delete;
    HANDLE get() const noexcept { return value_; }

private:
    HANDLE value_;
};

class SensitiveHeapBuffer final {
public:
    explicit SensitiveHeapBuffer(DWORD bytes) noexcept
        : bytes_(bytes), value_(static_cast<unsigned char*>(
              HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, bytes))) {}
    ~SensitiveHeapBuffer() noexcept {
        if (value_ != nullptr) {
            SecureZeroMemory(value_, bytes_);
            HeapFree(GetProcessHeap(), 0, value_);
        }
    }
    SensitiveHeapBuffer(const SensitiveHeapBuffer&) = delete;
    SensitiveHeapBuffer& operator=(const SensitiveHeapBuffer&) = delete;
    unsigned char* get() const noexcept { return value_; }

private:
    DWORD bytes_;
    unsigned char* value_;
};

class SensitiveLocalString final {
public:
    explicit SensitiveLocalString(LPWSTR value) noexcept : value_(value) {}
    ~SensitiveLocalString() noexcept {
        if (value_ != nullptr) {
            SecureZeroMemory(value_, (static_cast<size_t>(lstrlenW(value_)) + 1) * sizeof(wchar_t));
            LocalFree(value_);
        }
    }
    SensitiveLocalString(const SensitiveLocalString&) = delete;
    SensitiveLocalString& operator=(const SensitiveLocalString&) = delete;
    const wchar_t* get() const noexcept { return value_; }

private:
    LPWSTR value_;
};

bool SetPrivateProperty(MSIHANDLE session, const wchar_t* name, const wchar_t* value) noexcept {
    return MsiSetPropertyW(session, name, value) == ERROR_SUCCESS;
}

std::wstring CombinePath(const std::wstring& root, const wchar_t* relative) {
    std::wstring result(root);
    while (!result.empty() && (result.back() == L'\\' || result.back() == L'/')) {
        result.pop_back();
    }
    result.push_back(L'\\');
    result.append(relative);
    return result;
}

bool IsFile(const std::wstring& path) noexcept {
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES &&
           (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool IsStableVersion8(const wchar_t* name) noexcept {
    if (name == nullptr || name[0] != L'8' || name[1] != L'.' || name[2] == L'\0') {
        return false;
    }
    bool lastWasDot = true;
    for (size_t index = 2; name[index] != L'\0'; ++index) {
        const wchar_t value = name[index];
        if (value == L'.') {
            if (lastWasDot) return false;
            lastWasDot = true;
        } else if (value >= L'0' && value <= L'9') {
            lastWasDot = false;
        } else {
            return false;
        }
    }
    return !lastWasDot;
}

bool HasFramework8(
    const std::wstring& dotnetRoot,
    const wchar_t* frameworkName,
    const wchar_t* primaryMarker,
    const wchar_t* secondaryMarker = nullptr) {
    const auto frameworkRoot = CombinePath(
        dotnetRoot,
        (std::wstring(L"shared\\") + frameworkName).c_str());
    const auto searchPattern = CombinePath(frameworkRoot, L"8.*");
    WIN32_FIND_DATAW data{};
    UniqueFindHandle search(FindFirstFileW(searchPattern.c_str(), &data));
    if (search.get() == INVALID_HANDLE_VALUE) {
        return false;
    }
    do {
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0 ||
            !IsStableVersion8(data.cFileName)) {
            continue;
        }
        const auto versionRoot = CombinePath(frameworkRoot, data.cFileName);
        if (IsFile(CombinePath(versionRoot, primaryMarker)) &&
            (secondaryMarker == nullptr || IsFile(CombinePath(versionRoot, secondaryMarker)))) {
            return true;
        }
    } while (FindNextFileW(search.get(), &data));
    return false;
}

bool ReadRegisteredDotNetRoot(std::wstring& root) {
    std::array<wchar_t, 32768> buffer{};
    DWORD bytes = static_cast<DWORD>(buffer.size() * sizeof(wchar_t));
    const LONG result = RegGetValueW(
        HKEY_LOCAL_MACHINE,
        L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64",
        L"InstallLocation",
        RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY,
        nullptr,
        buffer.data(),
        &bytes);
    if (result != ERROR_SUCCESS || buffer[0] == L'\0') {
        return false;
    }
    root.assign(buffer.data());
    return !root.empty();
}

bool ReadProgramFilesDotNetRoot(std::wstring& root) {
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetEnvironmentVariableW(
        L"ProgramFiles", buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) {
        return false;
    }
    root = CombinePath(buffer.data(), L"dotnet");
    return true;
}

void ProbeDotNet8(bool& core, bool& desktop) {
    core = false;
    desktop = false;
    std::array<std::wstring, 2> roots{};
    size_t count = 0;
    if (ReadRegisteredDotNetRoot(roots[count])) ++count;
    std::wstring fallback;
    if (ReadProgramFilesDotNetRoot(fallback) &&
        (count == 0 || _wcsicmp(roots[0].c_str(), fallback.c_str()) != 0)) {
        roots[count++] = fallback;
    }
    for (size_t index = 0; index < count && (!core || !desktop); ++index) {
        if (!core) {
            core = HasFramework8(
                roots[index], L"Microsoft.NETCore.App", L"System.Private.CoreLib.dll", L"hostpolicy.dll");
        }
        if (!desktop) {
            desktop = HasFramework8(
                roots[index], L"Microsoft.WindowsDesktop.App", L"PresentationFramework.dll");
        }
    }
}

void ClearUserProperties(MSIHANDLE session) noexcept {
    SetPrivateProperty(session, kReadyProperty, L"");
    SetPrivateProperty(session, kSidProperty, L"");
    SetPrivateProperty(session, kSessionProperty, L"");
    SetPrivateProperty(session, kNonceProperty, L"");
}

void DisableUserContext(MSIHANDLE session) noexcept {
    ClearUserProperties(session);
    SetPrivateProperty(session, kReadyProperty, L"0");
}

void ClearPrivateProperties(MSIHANDLE session) noexcept {
    ClearUserProperties(session);
    SetPrivateProperty(session, kMachineReadyProperty, L"");
    SetPrivateProperty(session, kDeadlineProperty, L"");
    SetPrivateProperty(session, kActionProperty, L"");
    SetPrivateProperty(session, kRootPolicyProperty, L"");
}

bool ReadProperty(MSIHANDLE session, const wchar_t* name, std::wstring& value) {
    DWORD length = 0;
    wchar_t probe = L'\0';
    const auto first = MsiGetPropertyW(session, name, &probe, &length);
    if (first != ERROR_SUCCESS && first != ERROR_MORE_DATA) {
        return false;
    }

    std::wstring buffer(static_cast<size_t>(length) + 1, L'\0');
    DWORD capacity = length + 1;
    if (MsiGetPropertyW(session, name, buffer.data(), &capacity) != ERROR_SUCCESS) {
        return false;
    }
    buffer.resize(capacity);
    value.swap(buffer);
    return true;
}

bool IsIndividualAccountSid(PSID sid) noexcept {
    if (sid == nullptr || !IsValidSid(sid) ||
        IsWellKnownSid(sid, WinLocalSystemSid) ||
        IsWellKnownSid(sid, WinLocalServiceSid) ||
        IsWellKnownSid(sid, WinNetworkServiceSid)) {
        return false;
    }

    const auto* authority = GetSidIdentifierAuthority(sid);
    const auto* count = GetSidSubAuthorityCount(sid);
    if (authority == nullptr || count == nullptr || *count == 0) {
        return false;
    }
    const bool ntAuthority =
        authority->Value[0] == 0 && authority->Value[1] == 0 &&
        authority->Value[2] == 0 && authority->Value[3] == 0 &&
        authority->Value[4] == 0 && authority->Value[5] == 5;
    if (ntAuthority && *GetSidSubAuthority(sid, 0) == SECURITY_SERVICE_ID_BASE_RID) {
        return false;
    }

    DWORD domainLength = SECURITY_MAX_SID_SIZE;
    alignas(DWORD) std::array<unsigned char, SECURITY_MAX_SID_SIZE> domain{};
    if (!GetWindowsAccountDomainSid(sid, domain.data(), &domainLength) ||
        EqualSid(sid, domain.data())) {
        SecureZeroMemory(domain.data(), domain.size());
        return false;
    }
    SecureZeroMemory(domain.data(), domain.size());
    return true;
}

FailureReason ReadValidatedTokenContext(
    HANDLE token,
    bool threadToken,
    std::wstring& sidText,
    DWORD& sessionId) {
    TOKEN_TYPE tokenType{};
    DWORD returned = 0;
    if (!GetTokenInformation(
            token, TokenType, &tokenType, sizeof(tokenType), &returned) ||
        tokenType != (threadToken ? TokenImpersonation : TokenPrimary)) {
        return threadToken
            ? FailureReason::ThreadTokenTypeInvalid
            : FailureReason::ProcessTokenTypeInvalid;
    }

    if (threadToken) {
        SECURITY_IMPERSONATION_LEVEL level{};
        if (!GetTokenInformation(
                token,
                TokenImpersonationLevel,
                &level,
                sizeof(level),
                &returned) ||
            level < SecurityImpersonation) {
            return FailureReason::ThreadImpersonationLevelInvalid;
        }
    }

    DWORD bytes = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || bytes == 0 || bytes > 65536) {
        return FailureReason::TokenUserQueryFailed;
    }

    SensitiveHeapBuffer tokenUserBytes(bytes);
    if (tokenUserBytes.get() == nullptr) {
        return FailureReason::TokenUserQueryFailed;
    }

    if (!GetTokenInformation(token, TokenUser, tokenUserBytes.get(), bytes, &bytes)) {
        return FailureReason::TokenUserQueryFailed;
    }

    DWORD returnedSession = 0;
    if (!GetTokenInformation(
            token,
            TokenSessionId,
            &returnedSession,
            sizeof(returnedSession),
            &returned) || returned != sizeof(returnedSession)) {
        return FailureReason::TokenSessionQueryFailed;
    }
    if (returnedSession == 0 || returnedSession > INT_MAX) {
        return FailureReason::TokenSessionNonInteractive;
    }
    if (!threadToken) {
        DWORD processSession = 0;
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &processSession) ||
            processSession == 0 ||
            processSession != returnedSession) {
            return FailureReason::ProcessTokenValidationFailed;
        }
    }

    DWORD isAppContainer = 0;
    if (!GetTokenInformation(
            token,
            TokenIsAppContainer,
            &isAppContainer,
            sizeof(isAppContainer),
            &returned) ||
        returned != sizeof(isAppContainer)) {
        return FailureReason::TokenUserQueryFailed;
    }
    if (isAppContainer != 0) {
        return FailureReason::TokenAppContainerRejected;
    }

    const auto* tokenUser = reinterpret_cast<const TOKEN_USER*>(tokenUserBytes.get());
    if (!IsIndividualAccountSid(tokenUser->User.Sid)) {
        return FailureReason::TokenSidInvalid;
    }

    LPWSTR converted = nullptr;
    if (!ConvertSidToStringSidW(tokenUser->User.Sid, &converted) || converted == nullptr) {
        return FailureReason::TokenSidInvalid;
    }
    SensitiveLocalString convertedSid(converted);
    sidText.assign(convertedSid.get());
    sessionId = returnedSession;
    return sidText.empty() ? FailureReason::TokenSidInvalid : FailureReason::ProcessTokenFallbackAccepted;
}

bool ShouldAttemptProcessToken(DWORD threadTokenError) noexcept {
    return threadTokenError == ERROR_NO_TOKEN;
}

FailureReason CaptureInstallerUserContext(
    std::wstring& sidText,
    DWORD& sessionId,
    bool& usedProcessToken,
    bool& threadTokenAbsent) {
    usedProcessToken = false;
    threadTokenAbsent = false;
    HANDLE rawToken = nullptr;
    if (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &rawToken)) {
        UniqueHandle token(rawToken);
        const auto result = ReadValidatedTokenContext(token.get(), true, sidText, sessionId);
        return result == FailureReason::ProcessTokenFallbackAccepted
            ? FailureReason::ProcessTokenFallbackAccepted
            : result;
    }

    const auto threadError = GetLastError();
    if (!ShouldAttemptProcessToken(threadError)) {
        return FailureReason::ThreadTokenOpenFailed;
    }
    threadTokenAbsent = true;

    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken)) {
        return FailureReason::ProcessTokenOpenFailed;
    }
    UniqueHandle processToken(rawToken);
    const auto result = ReadValidatedTokenContext(processToken.get(), false, sidText, sessionId);
    if (result == FailureReason::ProcessTokenFallbackAccepted) {
        usedProcessToken = true;
    }
    return result;
}

wchar_t HexDigit(unsigned value) noexcept {
    return static_cast<wchar_t>(value < 10 ? L'0' + value : L'A' + value - 10);
}

bool GenerateSessionNonce(std::wstring& nonce) {
    std::array<unsigned char, 16> bytes{};
    if (BCryptGenRandom(
            nullptr,
            bytes.data(),
            static_cast<ULONG>(bytes.size()),
            BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) {
        return false;
    }
    bytes[6] = static_cast<unsigned char>((bytes[6] & 0x0F) | 0x40);
    bytes[8] = static_cast<unsigned char>((bytes[8] & 0x3F) | 0x80);

    std::array<wchar_t, 37> text{};
    size_t output = 0;
    for (size_t index = 0; index < bytes.size(); ++index) {
        if (index == 4 || index == 6 || index == 8 || index == 10) {
            text[output++] = L'-';
        }
        text[output++] = HexDigit(bytes[index] >> 4);
        text[output++] = HexDigit(bytes[index] & 0x0F);
    }
    text[output] = L'\0';
    try {
        nonce.assign(text.data(), output);
    } catch (...) {
        SecureZeroMemory(bytes.data(), bytes.size());
        SecureZeroMemory(text.data(), text.size() * sizeof(wchar_t));
        throw;
    }
    SecureZeroMemory(bytes.data(), bytes.size());
    SecureZeroMemory(text.data(), text.size() * sizeof(wchar_t));
    return nonce.size() == 36;
}

bool ContainsAllToken(const std::wstring& value) {
    size_t start = 0;
    while (start <= value.size()) {
        const auto comma = value.find(L',', start);
        const auto length = comma == std::wstring::npos ? value.size() - start : comma - start;
        auto token = value.substr(start, length);
        const auto first = token.find_first_not_of(L" \t");
        const auto last = token.find_last_not_of(L" \t");
        if (first != std::wstring::npos) {
            token = token.substr(first, last - first + 1);
        }
        if (_wcsicmp(token.c_str(), L"ALL") == 0) {
            return true;
        }
        if (comma == std::wstring::npos) {
            break;
        }
        start = comma + 1;
    }
    return false;
}

bool DeriveInstallAction(MSIHANDLE session, std::wstring& action) {
    std::wstring remove;
    std::wstring upgradeDetected;
    std::wstring installed;
    if (!ReadProperty(session, L"REMOVE", remove) ||
        !ReadProperty(session, L"WIX_UPGRADE_DETECTED", upgradeDetected) ||
        !ReadProperty(session, L"Installed", installed)) {
        return false;
    }

    if (ContainsAllToken(remove)) {
        action = L"uninstall";
    } else if (!upgradeDetected.empty()) {
        action = L"upgrade";
    } else if (!installed.empty()) {
        action = L"repair";
    } else {
        action = L"install";
    }
    return true;
}

bool HexThumbprint(const std::wstring& text, std::array<unsigned char, 20>& bytes) noexcept {
    if (text.size() != bytes.size() * 2) return false;
    const auto nibble = [](wchar_t value) noexcept -> int {
        if (value >= L'0' && value <= L'9') return value - L'0';
        if (value >= L'A' && value <= L'F') return value - L'A' + 10;
        if (value >= L'a' && value <= L'f') return value - L'a' + 10;
        return -1;
    };
    for (size_t index = 0; index < bytes.size(); ++index) {
        const int high = nibble(text[index * 2]);
        const int low = nibble(text[index * 2 + 1]);
        if (high < 0 || low < 0) return false;
        bytes[index] = static_cast<unsigned char>((high << 4) | low);
    }
    return true;
}

bool ParseCertificateData(
    MSIHANDLE session,
    bool requirePath,
    std::array<unsigned char, 20>& thumbprint,
    std::wstring& path) {
    std::wstring data;
    if (!ReadProperty(session, L"CustomActionData", data)) return false;
    const auto separator = data.find(L'|');
    const std::wstring thumbprintText = separator == std::wstring::npos ? data : data.substr(0, separator);
    if (!HexThumbprint(thumbprintText, thumbprint)) return false;
    if (!requirePath) return separator == std::wstring::npos;
    if (separator == std::wstring::npos || separator + 1 >= data.size()) return false;
    path = data.substr(separator + 1);
    if (path.size() >= 32768 || path.find(L'|') != std::wstring::npos || path.find(L'\"') != std::wstring::npos ||
        path.size() < 7 || path[1] != L':' || (path[2] != L'\\' && path[2] != L'/') ||
        _wcsicmp(path.c_str() + path.size() - 4, L".cer") != 0) return false;
    return true;
}

bool ReadCertificateFile(const std::wstring& path, std::vector<unsigned char>& bytes) {
    UniqueHandle file(CreateFileW(
        path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    if (file.get() == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.get(), &size) || size.QuadPart <= 0 || size.QuadPart > 1024 * 1024) return false;
    bytes.resize(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    return ReadFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) && read == bytes.size();
}

bool CertificateThumbprint(PCCERT_CONTEXT certificate, std::array<unsigned char, 20>& thumbprint) noexcept {
    DWORD size = static_cast<DWORD>(thumbprint.size());
    return CertGetCertificateContextProperty(
        certificate, CERT_SHA1_HASH_PROP_ID, thumbprint.data(), &size) && size == thumbprint.size();
}

void LogCertificateResult(MSIHANDLE session, const wchar_t* operation, bool success) noexcept {
    MSIHANDLE record = MsiCreateRecord(1);
    if (!record) return;
    wchar_t message[160]{};
    swprintf_s(message, L"SwSearch modern context-menu certificate %ls: %ls", operation, success ? L"success" : L"failed");
    if (MsiRecordSetStringW(record, 0, message) == ERROR_SUCCESS)
        MsiProcessMessage(session, success ? INSTALLMESSAGE_INFO : INSTALLMESSAGE_ERROR, record);
    MsiCloseHandle(record);
}
} // namespace

extern "C" __declspec(dllexport) UINT __stdcall CheckSwSearchDotNet8(MSIHANDLE session) noexcept {
    if (session == 0) {
        return ERROR_INSTALL_FAILURE;
    }
    try {
        bool core = false;
        bool desktop = false;
        ProbeDotNet8(core, desktop);
        const bool propertiesWritten =
            SetPrivateProperty(session, kDotNetCore8Property, core ? L"1" : L"0") &&
            SetPrivateProperty(session, kWindowsDesktop8Property, desktop ? L"1" : L"0") &&
            SetPrivateProperty(session, kDotNet8ReadyProperty, core && desktop ? L"1" : L"0");
        return propertiesWritten ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    } catch (...) {
        SetPrivateProperty(session, kDotNetCore8Property, L"0");
        SetPrivateProperty(session, kWindowsDesktop8Property, L"0");
        SetPrivateProperty(session, kDotNet8ReadyProperty, L"0");
        return ERROR_INSTALL_FAILURE;
    }
}

extern "C" __declspec(dllexport) UINT __stdcall CaptureTrustedInstallerContext(MSIHANDLE session) noexcept {
    if (session == 0) {
        return ERROR_INSTALL_FAILURE;
    }

    ClearPrivateProperties(session);
    std::wstring sid;
    std::wstring sessionText;
    std::wstring nonce;
    const auto clearSensitive = [&]() noexcept {
        if (!sid.empty()) {
            SecureZeroMemory(sid.data(), sid.size() * sizeof(wchar_t));
        }
        if (!nonce.empty()) {
            SecureZeroMemory(nonce.data(), nonce.size() * sizeof(wchar_t));
        }
        if (!sessionText.empty()) {
            SecureZeroMemory(sessionText.data(), sessionText.size() * sizeof(wchar_t));
        }
    };
    try {
        std::wstring action;
        DWORD sessionId = 0;
        if (!DeriveInstallAction(session, action)) {
            LogReason(session, FailureReason::InstallActionInvalid, true);
            clearSensitive();
            ClearPrivateProperties(session);
            return ERROR_INSTALL_FAILURE;
        }

        const auto deadline = std::to_wstring(GetTickCount64() + kSessionDeadlineMilliseconds);
        const bool machineContextSuccess =
            SetPrivateProperty(session, kDeadlineProperty, deadline.c_str()) &&
            SetPrivateProperty(session, kActionProperty, action.c_str()) &&
            SetPrivateProperty(session, kRootPolicyProperty, kRegisteredRootPolicy) &&
            SetPrivateProperty(session, kReadyProperty, L"0") &&
            SetPrivateProperty(session, kMachineReadyProperty, L"1");
        if (!machineContextSuccess) {
            LogReason(session, FailureReason::MachineContextWriteFailed, true);
            ClearPrivateProperties(session);
            clearSensitive();
            return ERROR_INSTALL_FAILURE;
        }

        bool usedProcessToken = false;
        bool threadTokenAbsent = false;
        const auto tokenResult = CaptureInstallerUserContext(
            sid,
            sessionId,
            usedProcessToken,
            threadTokenAbsent);
        if (threadTokenAbsent) {
            LogReason(session, FailureReason::ThreadTokenAbsent, false);
        }
        if (tokenResult != FailureReason::ProcessTokenFallbackAccepted) {
            LogReason(session, tokenResult, false);
            DisableUserContext(session);
            clearSensitive();
            return ERROR_SUCCESS;
        }
        if (usedProcessToken) {
            LogReason(session, FailureReason::ProcessTokenFallbackAccepted, false);
        }

        sessionText = std::to_wstring(sessionId);
        if (!GenerateSessionNonce(nonce)) {
            LogReason(session, FailureReason::NonceGenerationFailed, false);
            DisableUserContext(session);
            clearSensitive();
            return ERROR_SUCCESS;
        }
        const bool userContextSuccess =
            SetPrivateProperty(session, kSidProperty, sid.c_str()) &&
            SetPrivateProperty(session, kSessionProperty, sessionText.c_str()) &&
            SetPrivateProperty(session, kNonceProperty, nonce.c_str()) &&
            SetPrivateProperty(session, kReadyProperty, L"1");
        clearSensitive();
        if (!userContextSuccess) {
            LogReason(session, FailureReason::PrivatePropertyWriteFailed, false);
            DisableUserContext(session);
        }
        return ERROR_SUCCESS;
    } catch (...) {
        LogReason(session, FailureReason::UnexpectedFailure, true);
        clearSensitive();
        ClearPrivateProperties(session);
        return ERROR_INSTALL_FAILURE;
    }
}

extern "C" __declspec(dllexport) UINT __stdcall SwSearchInstallerTokenPolicySelfTest() noexcept {
    // The process-token path is permitted only for the precise no-thread-token
    // case. Any other thread-token failure must remain fail-closed.
    if (!ShouldAttemptProcessToken(ERROR_NO_TOKEN) ||
        ShouldAttemptProcessToken(ERROR_ACCESS_DENIED) ||
        ShouldAttemptProcessToken(ERROR_BAD_TOKEN_TYPE)) {
        return ERROR_INVALID_DATA;
    }

    // These policy controls deliberately model the unsafe regressions the
    // production validation rejects before accepting a process-token fallback.
    const auto acceptableShape = [](TOKEN_TYPE type,
                                    DWORD session,
                                    bool individualSid,
                                    bool processSessionMatches) noexcept {
        return type == TokenPrimary &&
               session > 0 &&
               session <= INT_MAX &&
               individualSid &&
               processSessionMatches;
    };
    if (!acceptableShape(TokenPrimary, 1, true, true) ||
        acceptableShape(TokenImpersonation, 1, true, true) ||
        acceptableShape(TokenPrimary, 0, true, true) ||
        acceptableShape(TokenPrimary, 1, false, true) ||
        acceptableShape(TokenPrimary, 1, true, false)) {
        return ERROR_INVALID_DATA;
    }
    return ERROR_SUCCESS;
}

extern "C" __declspec(dllexport) UINT __stdcall InstallModernMenuCertificate(MSIHANDLE session) noexcept {
    if (!session) return ERROR_INSTALL_FAILURE;
    try {
        std::array<unsigned char, 20> expected{};
        std::wstring path;
        if (!ParseCertificateData(session, true, expected, path)) {
            LogCertificateResult(session, L"install-data", false);
            return ERROR_INSTALL_FAILURE;
        }
        std::vector<unsigned char> bytes;
        if (!ReadCertificateFile(path, bytes)) {
            LogCertificateResult(session, L"read", false);
            return ERROR_INSTALL_FAILURE;
        }
        PCCERT_CONTEXT certificate = CertCreateCertificateContext(
            X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, bytes.data(), static_cast<DWORD>(bytes.size()));
        if (!certificate) return ERROR_INSTALL_FAILURE;
        std::array<unsigned char, 20> actual{};
        const bool thumbprintMatches = CertificateThumbprint(certificate, actual) && actual == expected;
        HCERTSTORE store = thumbprintMatches ? CertOpenStore(
            CERT_STORE_PROV_SYSTEM_W, 0, 0,
            CERT_SYSTEM_STORE_LOCAL_MACHINE | CERT_STORE_OPEN_EXISTING_FLAG,
            L"TrustedPeople") : nullptr;
        bool success = store && CertAddCertificateContextToStore(
            store, certificate, CERT_STORE_ADD_REPLACE_EXISTING, nullptr);
        if (success) {
            PCCERT_CONTEXT current = nullptr;
            while ((current = CertEnumCertificatesInStore(store, current)) != nullptr) {
                std::array<unsigned char, 20> currentThumbprint{};
                if (CertCompareCertificateName(
                        X509_ASN_ENCODING, &current->pCertInfo->Subject, &certificate->pCertInfo->Subject) &&
                    CertificateThumbprint(current, currentThumbprint) && currentThumbprint != expected) {
                    PCCERT_CONTEXT duplicate = CertDuplicateCertificateContext(current);
                    if (duplicate) CertDeleteCertificateFromStore(duplicate);
                }
            }
        }
        if (store) CertCloseStore(store, 0);
        CertFreeCertificateContext(certificate);
        SecureZeroMemory(bytes.data(), bytes.size());
        LogCertificateResult(session, L"install", success);
        return success ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    } catch (...) {
        LogCertificateResult(session, L"install", false);
        return ERROR_INSTALL_FAILURE;
    }
}

extern "C" __declspec(dllexport) UINT __stdcall RemoveModernMenuCertificate(MSIHANDLE session) noexcept {
    if (!session) return ERROR_INSTALL_FAILURE;
    try {
        std::array<unsigned char, 20> thumbprint{};
        std::wstring unused;
        if (!ParseCertificateData(session, false, thumbprint, unused)) {
            LogCertificateResult(session, L"remove-data", false);
            return ERROR_INSTALL_FAILURE;
        }
        HCERTSTORE store = CertOpenStore(
            CERT_STORE_PROV_SYSTEM_W, 0, 0,
            CERT_SYSTEM_STORE_LOCAL_MACHINE | CERT_STORE_OPEN_EXISTING_FLAG,
            L"TrustedPeople");
        if (!store) return ERROR_INSTALL_FAILURE;
        CRYPT_HASH_BLOB hash{ static_cast<DWORD>(thumbprint.size()), thumbprint.data() };
        PCCERT_CONTEXT certificate = CertFindCertificateInStore(
            store, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0, CERT_FIND_SHA1_HASH, &hash, nullptr);
        const bool success = !certificate || CertDeleteCertificateFromStore(certificate);
        CertCloseStore(store, 0);
        LogCertificateResult(session, L"remove", success);
        return success ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    } catch (...) {
        LogCertificateResult(session, L"remove", false);
        return ERROR_INSTALL_FAILURE;
    }
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) noexcept {
    return TRUE;
}
