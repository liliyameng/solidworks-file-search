using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SwSearch.Contracts;

namespace SwSearch.Metadata.Worker;

internal sealed record ParsedSolidWorksMetadata(
    IReadOnlyList<DocumentProperty> Properties,
    IReadOnlyList<DocumentReference> References,
    IReadOnlyList<string> ConfigurationNames);

internal static class SolidWorksXmlMetadataParser
{
    public static ParsedSolidWorksMetadata Parse(
        IReadOnlyDictionary<string, byte[]> streams,
        ValidatedFile file,
        int internalVersion,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        if (!SupportedSolidWorksDocumentKinds.IsSupported(file.Candidate.Kind))
        {
            throw new MetadataWorkerException(MetadataErrorCodes.UnsupportedFileType);
        }

        var properties = new List<DocumentProperty>();
        var configurationNames = ResolveConfigurationNames(
            streams,
            internalVersion,
            limits,
            cancellationToken);

        if (streams.TryGetValue("docProps/custom.xml", out var globalXml))
        {
            properties.AddRange(ParsePropertySectionXml(
                globalXml,
                PropertyScope.Document,
                configurationName: null,
                limits,
                cancellationToken));
        }

        foreach (var (name, bytes) in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ModernSolidWorksContainer.TryReadConfigurationPropertyIndex(name, out var configIndex))
            {
                continue;
            }

            if (!configurationNames.TryGetValue(configIndex, out var configurationName) ||
                string.IsNullOrWhiteSpace(configurationName))
            {
                // A stream index is not a configuration name. Skipping an unresolved
                // scope avoids leaking placeholder names such as Config-0 or mixing
                // configuration-specific values into document properties.
                continue;
            }
            properties.AddRange(ParsePropertySectionXml(
                bytes,
                PropertyScope.Configuration,
                configurationName,
                limits,
                cancellationToken));
        }

        if (streams.TryGetValue("docProps/core.xml", out var coreXml))
        {
            AddCoreProperties(properties, coreXml, limits, cancellationToken);
        }

        if (streams.TryGetValue("docProps/app.xml", out var appXml))
        {
            AddAppProperties(properties, appXml, limits, cancellationToken);
        }

        if (streams.TryGetValue("docProps/ISolidWorksInformation.xml", out var informationXml))
        {
            AddSolidWorksInformation(properties, informationXml, limits, cancellationToken);
        }

        if (streams.TryGetValue("swXmlContents/MATERIALTREE", out var materialXml))
        {
            AddMaterialProperties(
                properties,
                configurationNames,
                materialXml,
                limits,
                cancellationToken);
        }

        if (properties.Count > limits.MaximumPropertyCount)
        {
            throw new InvalidDataException("Property count exceeds configured limits.");
        }

        return new ParsedSolidWorksMetadata(
            Deduplicate(properties),
            Array.Empty<DocumentReference>(),
            configurationNames.OrderBy(static item => item.Key).Select(static item => item.Value).ToArray());
    }

    internal static IReadOnlyList<DocumentProperty> ParsePropertySectionXml(
        byte[] xml,
        PropertyScope scope,
        string? configurationName,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var document = LoadXml(xml, limits, cancellationToken);
        var nodes = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "propertySection")
            .Where(static element =>
                string.Equals(
                    AttributeValue(element, "name"),
                    "UserDefinedProperties",
                    StringComparison.Ordinal))
            .SelectMany(static element => element.Elements())
            .Where(static element => element.Name.LocalName == "property")
            .Take(limits.MaximumPropertyCount)
            .ToArray();

        var linkedByPid = new Dictionary<uint, string>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = AttributeValue(node, "name");
            if (!string.IsNullOrEmpty(name) || !TryReadPid(node, out var pid) || pid < 0x01000000)
            {
                continue;
            }

            var linked = ReadPrimaryValue(node, limits);
            if (!string.IsNullOrEmpty(linked))
            {
                linkedByPid.TryAdd(pid & 0x00FFFFFF, linked);
            }
        }

        var properties = new List<DocumentProperty>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = AttributeValue(node, "name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > limits.MaximumStringCharacters)
            {
                continue;
            }

            var display = ReadPrimaryValue(node, limits);
            var cached = ReadCachedValue(node, limits);
            var hasPid = TryReadPid(node, out var pid);
            var linked = hasPid && linkedByPid.TryGetValue(pid, out var linkedValue)
                ? linkedValue
                : null;
            var equation = bool.TryParse(AttributeValue(node, "IsEquation"), out var isEquation) && isEquation;
            var resolved = !string.IsNullOrEmpty(cached) ? NormalizeCachedValue(cached) : display;
            properties.Add(new DocumentProperty(
                name,
                linked ?? display,
                resolved,
                scope,
                configurationName,
                MetadataSource.NoApi,
                OriginPath: null,
                DisplayValue: display,
                CachedValue: string.IsNullOrEmpty(cached) ? null : NormalizeCachedValue(cached),
                IsLinked: linked is not null || equation));
        }

        return properties;
    }

    private static Dictionary<int, string> ResolveConfigurationNames(
        IReadOnlyDictionary<string, byte[]> streams,
        int internalVersion,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        if (streams.TryGetValue("Contents/CMgrHdr2", out var managerHeader) ||
            streams.TryGetValue("Contents/CMgrHdr", out managerHeader))
        {
            foreach (var (index, name) in MfcConfigurationNameReader.Read(
                         managerHeader,
                         internalVersion,
                         limits))
            {
                result.TryAdd(index, name);
            }
        }

        if (streams.TryGetValue("swXmlContents/COMPINSTANCETREE", out var componentTree))
        {
            var document = LoadXml(componentTree, limits, cancellationToken);
            foreach (var model in document.Descendants().Where(static element =>
                         element.Name.LocalName.Equals("swModel", StringComparison.Ordinal)))
            {
                if (int.TryParse(
                        AttributeValue(model, "swConfigurationId"),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var index) &&
                    index >= 0)
                {
                    var name = AttributeValue(model, "swConfigurationName");
                    if (!string.IsNullOrWhiteSpace(name) && name.Length <= limits.MaximumStringCharacters)
                    {
                        result.TryAdd(index, name);
                    }
                }
            }
        }

        if (result.Count == 0 &&
            streams.TryGetValue("docProps/ISolidWorksInformation.xml", out var informationXml))
        {
            var information = ParseAllNamedProperties(informationXml, limits, cancellationToken);
            if (information.TryGetValue("SW-Configuration Name", out var activeName) &&
                !string.IsNullOrWhiteSpace(activeName))
            {
                result[0] = activeName;
            }
        }

        if (result.Count > limits.MaximumConfigurationCount)
        {
            throw new InvalidDataException("Configuration count exceeds configured limits.");
        }

        return result;
    }

    private static void AddCoreProperties(
        ICollection<DocumentProperty> destination,
        byte[] xml,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["creator"] = "Author",
            ["lastModifiedBy"] = "LastSavedBy",
            ["title"] = "Title",
            ["subject"] = "Subject",
            ["keywords"] = "Keywords",
            ["description"] = "Comments",
            ["created"] = "CreateDateTime",
            ["modified"] = "LastSaveDateTime"
        };
        var document = LoadXml(xml, limits, cancellationToken);
        foreach (var element in document.Root?.Elements() ?? [])
        {
            if (mappings.TryGetValue(element.Name.LocalName, out var propertyName))
            {
                AddSimpleProperty(destination, propertyName, Bounded(element.Value, limits), PropertyScope.Document, null);
            }
        }
    }

    private static void AddAppProperties(
        ICollection<DocumentProperty> destination,
        byte[] xml,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var document = LoadXml(xml, limits, cancellationToken);
        foreach (var element in document.Root?.Elements() ?? [])
        {
            if (element.Name.LocalName is "Application" or "AppVersion")
            {
                AddSimpleProperty(
                    destination,
                    element.Name.LocalName,
                    Bounded(element.Value, limits),
                    PropertyScope.Document,
                    null);
            }
        }
    }

    private static void AddSolidWorksInformation(
        ICollection<DocumentProperty> destination,
        byte[] xml,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        foreach (var (name, value) in ParseAllNamedProperties(xml, limits, cancellationToken))
        {
            if (name.StartsWith("SW-", StringComparison.OrdinalIgnoreCase))
            {
                AddSimpleProperty(destination, name, value, PropertyScope.Document, null);
            }
        }
    }

    private static Dictionary<string, string> ParseAllNamedProperties(
        byte[] xml,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = LoadXml(xml, limits, cancellationToken);
        foreach (var property in document.Descendants().Where(static element =>
                     element.Name.LocalName == "property").Take(limits.MaximumPropertyCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = AttributeValue(property, "name");
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= limits.MaximumStringCharacters)
            {
                result.TryAdd(name, ReadPrimaryValue(property, limits));
            }
        }

        return result;
    }

    private static void AddMaterialProperties(
        ICollection<DocumentProperty> destination,
        IDictionary<int, string> configurationNames,
        byte[] xml,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var document = LoadXml(xml, limits, cancellationToken);
        var materials = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in document.Descendants().Where(static element =>
                     element.Name.LocalName == "material"))
        {
            var name = AttributeValue(material, "name");
            var id = AttributeValue(material, "matid");
            if (!string.IsNullOrWhiteSpace(name))
            {
                materials.TryAdd($"{name}|{id}", material);
                materials.TryAdd(name, material);
            }
        }

        var configurations = document.Descendants().Where(static element =>
                element.Name.LocalName == "configuration")
            .Take(limits.MaximumConfigurationCount)
            .ToArray();
        if (configurationNames.Count == 0 && configurations.Length == 1)
        {
            var onlyName = AttributeValue(configurations[0], "name");
            if (!string.IsNullOrWhiteSpace(onlyName))
            {
                configurationNames[0] = onlyName;
            }
        }

        foreach (var configuration in configurations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configurationName = AttributeValue(configuration, "name");
            var materialReference = AttributeValue(configuration, "material");
            if (string.IsNullOrWhiteSpace(configurationName) ||
                string.IsNullOrWhiteSpace(materialReference) ||
                !materials.TryGetValue(materialReference, out var material))
            {
                continue;
            }

            var pieces = materialReference.Split('|');
            if (pieces.Length >= 2)
            {
                AddSimpleProperty(destination, "材料库", pieces[0], PropertyScope.Configuration, configurationName);
                AddSimpleProperty(destination, "材料名称", pieces[1], PropertyScope.Configuration, configurationName);
            }

            var classification = material.Ancestors().FirstOrDefault(static element =>
                element.Name.LocalName == "classification");
            var classificationName = classification is null ? string.Empty : AttributeValue(classification, "name");
            if (!string.IsNullOrWhiteSpace(classificationName))
            {
                AddSimpleProperty(destination, "材料分类", classificationName, PropertyScope.Configuration, configurationName);
            }

            var materialId = AttributeValue(material, "matid");
            if (!string.IsNullOrWhiteSpace(materialId))
            {
                AddSimpleProperty(destination, "材料ID", materialId, PropertyScope.Configuration, configurationName);
            }

            foreach (var physical in material.Descendants().Where(static element =>
                         element.Parent?.Name.LocalName == "physicalproperties"))
            {
                var displayName = AttributeValue(physical, "displayname");
                var value = AttributeValue(physical, "value");
                if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(value))
                {
                    AddSimpleProperty(destination, displayName, value, PropertyScope.Configuration, configurationName);
                }
            }
        }
    }

    private static XDocument LoadXml(
        byte[] bytes,
        NoApiExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("Metadata XML is empty.");
        }

        using var input = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaximumXmlCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        try
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Invalid metadata XML.", exception);
        }
    }

    private static string ReadPrimaryValue(XElement property, NoApiExtractionLimits limits)
    {
        var valueNode = property.Elements().FirstOrDefault(static element =>
            element.Name.LocalName != "FPVals");
        return Bounded(valueNode?.Value ?? string.Empty, limits);
    }

    private static string ReadCachedValue(XElement property, NoApiExtractionLimits limits)
    {
        var values = property.Elements().FirstOrDefault(static element =>
            element.Name.LocalName == "FPVals");
        return Bounded(values?.Descendants().FirstOrDefault()?.Value ?? string.Empty, limits);
    }

    private static string NormalizeCachedValue(string value)
    {
        if (value.StartsWith("<$", StringComparison.Ordinal) &&
            value.EndsWith("$>", StringComparison.Ordinal))
        {
            var secondDollar = value.IndexOf('$', 2);
            if (secondDollar >= 0 && secondDollar + 1 <= value.Length - 2)
            {
                return value.Substring(secondDollar + 1, value.Length - secondDollar - 3);
            }
        }

        return value;
    }

    private static bool TryReadPid(XElement property, out uint pid) =>
        uint.TryParse(
            AttributeValue(property, "pid"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out pid);

    private static void AddSimpleProperty(
        ICollection<DocumentProperty> destination,
        string name,
        string value,
        PropertyScope scope,
        string? configurationName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        destination.Add(new DocumentProperty(
            name,
            value,
            value,
            scope,
            configurationName,
            MetadataSource.NoApi,
            DisplayValue: value,
            CachedValue: value));
    }

    private static IReadOnlyList<DocumentProperty> Deduplicate(
        IEnumerable<DocumentProperty> properties)
    {
        var result = new List<DocumentProperty>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            var key = string.Concat(
                ((int)property.Scope).ToString(CultureInfo.InvariantCulture),
                "\0",
                property.ConfigurationName,
                "\0",
                property.OriginPath,
                "\0",
                property.Name);
            if (keys.Add(key))
            {
                result.Add(property);
            }
        }

        return result;
    }

    private static string AttributeValue(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(localName, StringComparison.Ordinal))?.Value ?? string.Empty;

    private static string Bounded(string value, NoApiExtractionLimits limits)
    {
        if (value.Length > limits.MaximumStringCharacters)
        {
            throw new InvalidDataException("Metadata string exceeds configured limits.");
        }

        return value;
    }
}
