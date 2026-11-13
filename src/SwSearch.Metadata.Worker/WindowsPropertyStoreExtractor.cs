using System.Runtime.InteropServices;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal sealed record PropertyStoreExtraction(
    IReadOnlyList<DocumentProperty> Properties,
    string? WarningCode);

internal interface IWindowsPropertyStoreExtractor
{
    PropertyStoreExtraction Extract(ValidatedFile file);
}

internal sealed class WindowsPropertyStoreExtractor : IWindowsPropertyStoreExtractor
{
    private const int MaximumProperties = 512;
    private const int MaximumValueLength = 65_536;
    private static readonly Guid PropertyStoreInterfaceId =
        new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    public PropertyStoreExtraction Extract(ValidatedFile file)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PropertyStoreExtraction(
                Array.Empty<DocumentProperty>(),
                MetadataErrorCodes.PropertyStoreUnavailable);
        }

        IPropertyStore? store = null;
        try
        {
            var interfaceId = PropertyStoreInterfaceId;
            var result = SHGetPropertyStoreFromParsingName(
                file.FullPath,
                IntPtr.Zero,
                GetPropertyStoreFlags.BestEffort,
                ref interfaceId,
                out store);
            Marshal.ThrowExceptionForHR(result);

            Marshal.ThrowExceptionForHR(store.GetCount(out var count));
            var properties = new List<DocumentProperty>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < Math.Min(count, MaximumProperties); index++)
            {
                Marshal.ThrowExceptionForHR(store.GetAt(index, out var key));
                var variant = default(PropertyVariant);
                try
                {
                    Marshal.ThrowExceptionForHR(store.GetValue(ref key, out variant));
                    var value = VariantToString(ref variant);
                    if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumValueLength)
                    {
                        continue;
                    }

                    var name = GetCanonicalName(ref key) ?? key.ToString();
                    if (!seen.Add(name))
                    {
                        continue;
                    }

                    properties.Add(new DocumentProperty(
                        name,
                        value,
                        value,
                        PropertyScope.Document,
                        ConfigurationName: null,
                        MetadataSource.WindowsPropertyStore));
                }
                finally
                {
                    _ = PropVariantClear(ref variant);
                }
            }

            return new PropertyStoreExtraction(
                properties,
                properties.Count == 0 ? MetadataErrorCodes.PropertyStoreEmpty : null);
        }
        catch (UnauthorizedAccessException)
        {
            return new PropertyStoreExtraction(
                Array.Empty<DocumentProperty>(),
                MetadataErrorCodes.FileAccessDenied);
        }
        catch (COMException exception) when ((exception.HResult & 0xFFFF) == 5)
        {
            return new PropertyStoreExtraction(
                Array.Empty<DocumentProperty>(),
                MetadataErrorCodes.FileAccessDenied);
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new PropertyStoreExtraction(
                Array.Empty<DocumentProperty>(),
                MetadataErrorCodes.PropertyStoreUnavailable);
        }
        finally
        {
            if (store is not null && Marshal.IsComObject(store))
            {
                _ = Marshal.FinalReleaseComObject(store);
            }
        }
    }

    private static string? VariantToString(ref PropertyVariant variant)
    {
        var result = PropVariantToStringAlloc(ref variant, out var pointer);
        if (result < 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string? GetCanonicalName(ref PropertyKey key)
    {
        var result = PSGetNameFromPropertyKey(ref key, out var pointer);
        if (result < 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindingContext,
        GetPropertyStoreFlags flags,
        ref Guid requestedInterface,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PSGetNameFromPropertyKey(
        ref PropertyKey key,
        out IntPtr canonicalName);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToStringAlloc(
        ref PropertyVariant variant,
        out IntPtr value);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropertyVariant variant);

    [Flags]
    private enum GetPropertyStoreFlags : uint
    {
        BestEffort = 0x40
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropertyVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropertyVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public override readonly string ToString() => $"{FormatId:B}:{PropertyId}";
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;

        [FieldOffset(8)]
        public long LongValue;
    }
}

