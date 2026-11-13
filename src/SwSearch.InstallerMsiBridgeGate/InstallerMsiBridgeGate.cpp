#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <bcrypt.h>
#include <msi.h>

#include <array>
#include <cwchar>
#include <cwctype>
#include <string>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

namespace {
constexpr DWORD kMaximumBridgeBytes = 16 * 1024 * 1024;
constexpr UINT kExpectedFailClosedResult = ERROR_INSTALL_FAILURE;

class UniqueHandle final {
public:
    explicit UniqueHandle(HANDLE value = INVALID_HANDLE_VALUE) noexcept : value_(value) {}
    ~UniqueHandle() noexcept {
        if (value_ != nullptr && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_);
    }
    HANDLE get() const noexcept { return value_; }
private:
    HANDLE value_;
};

class AlgorithmHandle final {
public:
    ~AlgorithmHandle() noexcept { if (value_ != nullptr) BCryptCloseAlgorithmProvider(value_, 0); }
    BCRYPT_ALG_HANDLE* address() noexcept { return &value_; }
    BCRYPT_ALG_HANDLE get() const noexcept { return value_; }
private:
    BCRYPT_ALG_HANDLE value_ = nullptr;
};

class HashHandle final {
public:
    ~HashHandle() noexcept { if (value_ != nullptr) BCryptDestroyHash(value_); }
    BCRYPT_HASH_HANDLE* address() noexcept { return &value_; }
    BCRYPT_HASH_HANDLE get() const noexcept { return value_; }
private:
    BCRYPT_HASH_HANDLE value_ = nullptr;
};

wchar_t Hex(unsigned value) noexcept {
    return static_cast<wchar_t>(value < 10 ? L'0' + value : L'A' + value - 10);
}

bool ComputeSha256(const wchar_t* path, std::wstring& result) {
    UniqueHandle file(CreateFileW(
        path,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr));
    if (file.get() == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.get(), &size) || size.QuadPart <= 0 || size.QuadPart > kMaximumBridgeBytes) {
        return false;
    }

    AlgorithmHandle algorithm;
    if (BCryptOpenAlgorithmProvider(algorithm.address(), BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) {
        return false;
    }
    DWORD objectBytes = 0;
    DWORD returned = 0;
    if (BCryptGetProperty(
            algorithm.get(),
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&objectBytes),
            sizeof(objectBytes),
            &returned,
            0) != 0 || objectBytes == 0 || returned != sizeof(objectBytes)) {
        return false;
    }

    std::vector<unsigned char> hashObject(objectBytes);
    HashHandle hash;
    if (BCryptCreateHash(
            algorithm.get(),
            hash.address(),
            hashObject.data(),
            static_cast<ULONG>(hashObject.size()),
            nullptr,
            0,
            0) != 0) {
        return false;
    }

    std::array<unsigned char, 64 * 1024> buffer{};
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(file.get(), buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) {
            SecureZeroMemory(buffer.data(), buffer.size());
            return false;
        }
        if (read == 0) break;
        if (BCryptHashData(hash.get(), buffer.data(), read, 0) != 0) {
            SecureZeroMemory(buffer.data(), buffer.size());
            return false;
        }
    }
    SecureZeroMemory(buffer.data(), buffer.size());

    std::array<unsigned char, 32> digest{};
    if (BCryptFinishHash(hash.get(), digest.data(), static_cast<ULONG>(digest.size()), 0) != 0) {
        return false;
    }
    std::array<wchar_t, 65> hex{};
    for (size_t index = 0; index < digest.size(); ++index) {
        hex[index * 2] = Hex(digest[index] >> 4);
        hex[index * 2 + 1] = Hex(digest[index] & 0x0F);
    }
    result.assign(hex.data(), 64);
    SecureZeroMemory(digest.data(), digest.size());
    SecureZeroMemory(hex.data(), hex.size() * sizeof(wchar_t));
    return true;
}

bool IsSha256Text(const wchar_t* value) noexcept {
    if (value == nullptr || wcslen(value) != 64) return false;
    for (size_t index = 0; index < 64; ++index) {
        if (!iswxdigit(value[index])) return false;
    }
    return true;
}
} // namespace

int wmain(int argc, wchar_t** argv) noexcept {
    if (argc != 3 || argv == nullptr || !IsSha256Text(argv[2])) return 64;
    try {
        std::wstring actualHash;
        if (!ComputeSha256(argv[1], actualHash) || _wcsicmp(actualHash.c_str(), argv[2]) != 0) {
            return 65;
        }

        HMODULE module = LoadLibraryExW(
            argv[1],
            nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (module == nullptr) return 66;
        const auto entry = reinterpret_cast<UINT(WINAPI*)(MSIHANDLE)>(
            GetProcAddress(module, "CaptureTrustedInstallerContext"));
        const auto policySelfTest = reinterpret_cast<UINT(WINAPI*)()>(
            GetProcAddress(module, "SwSearchInstallerTokenPolicySelfTest"));
        if (entry == nullptr || policySelfTest == nullptr) {
            FreeLibrary(module);
            return 67;
        }

        const UINT policyResult = policySelfTest();
        const UINT result = entry(0);
        const BOOL unloaded = FreeLibrary(module);
        return policyResult == ERROR_SUCCESS &&
               result == kExpectedFailClosedResult &&
               unloaded ? 0 : 68;
    } catch (...) {
        return 70;
    }
}
