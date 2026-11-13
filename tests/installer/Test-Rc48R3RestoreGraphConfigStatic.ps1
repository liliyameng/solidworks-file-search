$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$utf8Strict = [Text.UTF8Encoding]::new($false, $true)
$script:configMutationCount = 0
$script:graphMutationCount = 0

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw "RC48_R3_RESTORE_GRAPH_CONFIG_STATIC_GATE: $Message"
    }
}

function Resolve-WorkspaceFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    Assert-True -Condition (
        $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) `
        -Message "path escaped workspace: $RelativePath"
    Assert-True -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
        -Message "file missing: $RelativePath"
    return $path
}

function Read-WorkspaceText {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Resolve-WorkspaceFile -RelativePath $RelativePath
    return [IO.File]::ReadAllText($path, $utf8Strict)
}

function ConvertFrom-SafeXmlText {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 1024 * 1024
    $stringReader = [IO.StringReader]::new($Text)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($stringReader, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.PreserveWhitespace = $true
        $document.Load($reader)
        return ,$document
    }
    catch {
        throw "$Label is not safe, well-formed XML: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        $stringReader.Dispose()
    }
}

function Assert-SafeDocumentEnvelope {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-True -Condition ($null -eq $Document.DocumentType) `
        -Message "$Label contains a document type"
    $elementCount = 0
    $declarationCount = 0
    foreach ($child in @($Document.ChildNodes)) {
        switch ($child.NodeType) {
            ([Xml.XmlNodeType]::Element) {
                $elementCount++
                break
            }
            ([Xml.XmlNodeType]::XmlDeclaration) {
                $declarationCount++
                break
            }
            ([Xml.XmlNodeType]::Whitespace) { break }
            ([Xml.XmlNodeType]::SignificantWhitespace) { break }
            ([Xml.XmlNodeType]::Text) {
                Assert-True -Condition ([String]::IsNullOrWhiteSpace($child.Value)) `
                    -Message "$Label contains document-level text"
                break
            }
            default {
                throw "RC48_R3_RESTORE_GRAPH_CONFIG_STATIC_GATE: $Label contains an unexpected document node: $($child.NodeType)"
            }
        }
    }
    Assert-True -Condition ($elementCount -eq 1 -and $declarationCount -le 1) `
        -Message "$Label document envelope drifted"
}

function Assert-NoNamespaces {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)][string]$Label
    )

    foreach ($element in @($Document.SelectNodes('//*'))) {
        Assert-True -Condition (
            [string]$element.Prefix -ceq '' -and
            [string]$element.NamespaceURI -ceq '') `
            -Message "$Label contains a namespaced element: $($element.LocalName)"
        foreach ($attribute in @($element.Attributes)) {
            Assert-True -Condition (
                [string]$attribute.Prefix -ceq '' -and
                [string]$attribute.NamespaceURI -ceq '') `
                -Message "$Label contains a namespaced attribute: $($attribute.Name)"
        }
    }
}

function Get-StrictElementChildren {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlNode]$Parent,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $elements = [Collections.Generic.List[Xml.XmlElement]]::new()
    foreach ($child in @($Parent.ChildNodes)) {
        switch ($child.NodeType) {
            ([Xml.XmlNodeType]::Element) {
                [void]$elements.Add([Xml.XmlElement]$child)
                break
            }
            ([Xml.XmlNodeType]::Whitespace) { break }
            ([Xml.XmlNodeType]::SignificantWhitespace) { break }
            ([Xml.XmlNodeType]::Text) {
                Assert-True -Condition ([String]::IsNullOrWhiteSpace($child.Value)) `
                    -Message "$Label contains text"
                break
            }
            default {
                throw "RC48_R3_RESTORE_GRAPH_CONFIG_STATIC_GATE: $Label contains an unexpected child node: $($child.NodeType)"
            }
        }
    }
    return @($elements)
}

function Assert-ExactSequence {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Actual,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-True -Condition ($Actual.Count -eq $Expected.Count) `
        -Message "$Label count was $($Actual.Count), expected $($Expected.Count)"
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-True -Condition (
            [string]$Actual[$index] -ceq [string]$Expected[$index]) `
            -Message "$Label order/value drifted at index $index"
    }
}

function Assert-ExactAttributes {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlElement]$Element,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedNames,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actualNames = @($Element.Attributes | ForEach-Object { [string]$_.Name })
    Assert-ExactSequence -Actual $actualNames -Expected $ExpectedNames `
        -Label "$Label attributes"
}

function Assert-LeafElement {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlElement]$Element,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $children = @(Get-StrictElementChildren -Parent $Element -Label $Label)
    Assert-True -Condition ($children.Count -eq 0) `
        -Message "$Label must not contain elements"
}

function Assert-TextOnlyElement {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlElement]$Element,
        [Parameter(Mandatory = $true)][string]$Label
    )

    foreach ($child in @($Element.ChildNodes)) {
        Assert-True -Condition (
            $child.NodeType -eq [Xml.XmlNodeType]::Text -or
            $child.NodeType -eq [Xml.XmlNodeType]::Whitespace -or
            $child.NodeType -eq [Xml.XmlNodeType]::SignificantWhitespace) `
            -Message "$Label contains a non-text child node: $($child.NodeType)"
    }
}

function Assert-LocalRelativeLiteral {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-True -Condition ($Value -ceq $Expected) `
        -Message "$Label value drifted"
    Assert-True -Condition (-not [IO.Path]::IsPathRooted($Value)) `
        -Message "$Label became absolute"
    Assert-True -Condition (
        -not $Value.StartsWith('\\', [StringComparison]::Ordinal) -and
        -not $Value.StartsWith('//', [StringComparison]::Ordinal)) `
        -Message "$Label became a UNC path"
    Assert-True -Condition (
        $Value -notmatch '^[A-Za-z][A-Za-z0-9+.-]*:' -and
        $Value.IndexOf('://', [StringComparison]::Ordinal) -lt 0) `
        -Message "$Label became a URI"
    Assert-True -Condition (
        $Value.IndexOf('$(', [StringComparison]::Ordinal) -lt 0 -and
        $Value.IndexOf('${', [StringComparison]::Ordinal) -lt 0 -and
        $Value.IndexOf('$env:', [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
        $Value -notmatch '%[^%]+%') `
        -Message "$Label contains variable expansion"
    Assert-True -Condition (
        $Value -notmatch '(?i)(?:username|password|cleartextpassword|credential|secret|token|api[-_]?key)' -and
        $Value.IndexOf('@', [StringComparison]::Ordinal) -lt 0) `
        -Message "$Label contains credentials"
}

function Assert-NuGetConfigText {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$ExpectedSource,
        [Parameter(Mandatory = $true)][string]$ExpectedPackages,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $document = ConvertFrom-SafeXmlText -Text $Text -Label $Label
    Assert-SafeDocumentEnvelope -Document $document -Label $Label
    Assert-NoNamespaces -Document $document -Label $Label
    $configuration = $document.DocumentElement
    Assert-True -Condition (
        $configuration.LocalName -ceq 'configuration' -and
        $configuration.Attributes.Count -eq 0) `
        -Message "$Label root drifted"

    $sections = @(Get-StrictElementChildren -Parent $configuration `
            -Label "$Label configuration")
    Assert-ExactSequence -Actual @($sections | ForEach-Object LocalName) `
        -Expected @('packageSources', 'fallbackPackageFolders', 'config') `
        -Label "$Label section"
    foreach ($section in $sections) {
        Assert-ExactAttributes -Element $section -ExpectedNames @() `
            -Label "$Label $($section.LocalName)"
    }

    $sourceChildren = @(Get-StrictElementChildren -Parent $sections[0] `
            -Label "$Label packageSources")
    Assert-ExactSequence -Actual @($sourceChildren | ForEach-Object LocalName) `
        -Expected @('clear', 'add') -Label "$Label packageSources children"
    Assert-ExactAttributes -Element $sourceChildren[0] -ExpectedNames @() `
        -Label "$Label packageSources clear"
    Assert-LeafElement -Element $sourceChildren[0] `
        -Label "$Label packageSources clear"
    Assert-ExactAttributes -Element $sourceChildren[1] `
        -ExpectedNames @('key', 'value') -Label "$Label package source"
    Assert-LeafElement -Element $sourceChildren[1] `
        -Label "$Label package source"
    Assert-True -Condition (
        $sourceChildren[1].GetAttribute('key') -ceq 'SwSearchOffline') `
        -Message "$Label source key drifted"
    Assert-LocalRelativeLiteral -Value $sourceChildren[1].GetAttribute('value') `
        -Expected $ExpectedSource -Label "$Label source"

    $fallbackChildren = @(Get-StrictElementChildren -Parent $sections[1] `
            -Label "$Label fallbackPackageFolders")
    Assert-ExactSequence -Actual @($fallbackChildren | ForEach-Object LocalName) `
        -Expected @('clear') -Label "$Label fallback children"
    Assert-ExactAttributes -Element $fallbackChildren[0] -ExpectedNames @() `
        -Label "$Label fallback clear"
    Assert-LeafElement -Element $fallbackChildren[0] `
        -Label "$Label fallback clear"

    $configChildren = @(Get-StrictElementChildren -Parent $sections[2] `
            -Label "$Label config")
    Assert-ExactSequence -Actual @($configChildren | ForEach-Object LocalName) `
        -Expected @('add', 'add') -Label "$Label config children"
    foreach ($entry in $configChildren) {
        Assert-ExactAttributes -Element $entry -ExpectedNames @('key', 'value') `
            -Label "$Label config add"
        Assert-LeafElement -Element $entry -Label "$Label config add"
    }
    Assert-True -Condition (
        $configChildren[0].GetAttribute('key') -ceq 'globalPackagesFolder') `
        -Message "$Label globalPackagesFolder key/order drifted"
    Assert-LocalRelativeLiteral -Value $configChildren[0].GetAttribute('value') `
        -Expected $ExpectedPackages -Label "$Label globalPackagesFolder"
    Assert-True -Condition (
        $configChildren[1].GetAttribute('key') -ceq 'auditMode' -and
        $configChildren[1].GetAttribute('value') -ceq 'direct') `
        -Message "$Label auditMode drifted"
}

function ConvertTo-ExactSemicolonList {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $parts = @($Value.Split([char]';'))
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($part in $parts) {
        Assert-True -Condition (
            $part.Length -gt 0 -and $part -ceq $part.Trim()) `
            -Message "$Label contains an empty or padded entry"
        Assert-True -Condition ($seen.Add($part)) `
            -Message "$Label contains a duplicate: $part"
    }
    return $parts
}

function ConvertTo-ExactPropertyMap {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $map = @{}
    $keys = [Collections.Generic.List[string]]::new()
    foreach ($rawEntry in @($Value.Split([char]';'))) {
        $entry = $rawEntry.Trim()
        Assert-True -Condition ($entry.Length -gt 0) `
            -Message "$Label contains an empty entry"
        $equalsIndex = $entry.IndexOf('=')
        Assert-True -Condition ($equalsIndex -gt 0) `
            -Message "$Label contains a malformed entry: $entry"
        $key = $entry.Substring(0, $equalsIndex)
        $propertyValue = $entry.Substring($equalsIndex + 1)
        Assert-True -Condition (
            $key -ceq $key.Trim() -and
            $propertyValue -ceq $propertyValue.Trim()) `
            -Message "$Label contains padded key/value text"
        Assert-True -Condition (-not $map.ContainsKey($key)) `
            -Message "$Label contains a duplicate: $key"
        $map[$key] = $propertyValue
        [void]$keys.Add($key)
    }
    return [pscustomobject]@{
        Keys = @($keys)
        Map = $map
    }
}

function Get-MSBuildPropertyReference {
    param([Parameter(Mandatory = $true)][string]$Name)
    return '$(' + $Name + ')'
}

function New-CanonicalPathCondition {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$Expected
    )
    $sourceReference = Get-MSBuildPropertyReference $Source
    $actualReference = Get-MSBuildPropertyReference $Actual
    $expectedReference = Get-MSBuildPropertyReference $Expected
    return "'$sourceReference' != '$expectedReference' Or '$actualReference' != '$expectedReference'"
}

function New-NotEqualCondition {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Expected
    )
    return "'" + (Get-MSBuildPropertyReference $Name) + "' != '$Expected'"
}

function New-NonEmptyPropertyCondition {
    param([Parameter(Mandatory = $true)][string]$Name)
    return "'" + (Get-MSBuildPropertyReference $Name) + "' != ''"
}

function New-GetFullPathExpression {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '$([System.IO.Path]::GetFullPath(' + "'" + $Value + "'" + '))'
}

function New-CopyUpperExpression {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '$([System.String]::Copy(' + "'" + $Value + "'" +
        ').ToUpperInvariant())'
}

function New-GetFileNameExpression {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '$([System.IO.Path]::GetFileName(' + "'" + $Value + "'" + '))'
}

function New-StartsWithExpression {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Prefix
    )
    return '$([System.String]::Copy(' + "'" + $Value + "'" +
        ').StartsWith(' + "'" + $Prefix + "'" + '))'
}

function New-GraphPropertyRecord {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Body,
        [AllowEmptyString()]
        [string]$Condition
    )

    return [pscustomobject]@{
        Name = $Name
        Body = $Body
        HasCondition = $PSBoundParameters.ContainsKey('Condition')
        Condition = if ($PSBoundParameters.ContainsKey('Condition')) {
            $Condition
        }
        else {
            ''
        }
    }
}

function Assert-RestoreGraphText {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $document = ConvertFrom-SafeXmlText -Text $Text -Label $Label
    Assert-SafeDocumentEnvelope -Document $document -Label $Label
    Assert-NoNamespaces -Document $document -Label $Label
    $project = $document.DocumentElement
    Assert-True -Condition ($project.LocalName -ceq 'Project') `
        -Message "$Label root is not Project"
    Assert-ExactAttributes -Element $project `
        -ExpectedNames @('TreatAsLocalProperty') -Label "$Label Project"

    $expectedLocalProperties = @(
        '_ExpectedRestoreConfigFile',
        '_ExpectedRestorePackagesPath',
        '_ExpectedRestoreSources',
        '_ExpectedMSBuildSDKsPath',
        '_ExpectedMSBuildExtensionsPath',
        '_ExpectedDirectoryBuildPropsPath',
        '_ExpectedTestResultsPathPrefix',
        '_ExpectedTestResultsPathPrefixUpper',
        '_ActualRestoreConfigFile',
        '_ActualRestorePackagesPath',
        '_ActualRestoreSources',
        '_ActualNuGetPackages',
        '_ActualMSBuildSDKsPath',
        '_ActualMSBuildExtensionsPath',
        '_ActualMSBuildUserExtensionsPath',
        '_ActualMSBuildUserExtensionsPathUpper',
        '_ActualMSBuildUserExtensionsLeaf',
        '_ActualMSBuildUserExtensionsUnderTestResults',
        '_ActualDirectoryBuildPropsPath'
    )
    $actualLocalProperties = @(ConvertTo-ExactSemicolonList `
            -Value $project.GetAttribute('TreatAsLocalProperty') `
            -Label "$Label TreatAsLocalProperty")
    Assert-ExactSequence -Actual $actualLocalProperties `
        -Expected $expectedLocalProperties -Label "$Label TreatAsLocalProperty"

    $projectChildren = @(Get-StrictElementChildren -Parent $project `
            -Label "$Label Project")
    Assert-ExactSequence -Actual @($projectChildren | ForEach-Object LocalName) `
        -Expected @('PropertyGroup', 'ItemGroup', 'Target', 'Target') `
        -Label "$Label Project children"

    $propertyGroup = $projectChildren[0]
    Assert-ExactAttributes -Element $propertyGroup -ExpectedNames @() `
        -Label "$Label PropertyGroup"
    $propertyElements = @(Get-StrictElementChildren -Parent $propertyGroup `
            -Label "$Label PropertyGroup")
    $msbuildThisFileDirectory =
        Get-MSBuildPropertyReference 'MSBuildThisFileDirectory'
    $expectedTestResultsPathPrefix =
        Get-MSBuildPropertyReference '_ExpectedTestResultsPathPrefix'
    $expectedTestResultsPathPrefixUpper =
        Get-MSBuildPropertyReference '_ExpectedTestResultsPathPrefixUpper'
    $actualMSBuildUserExtensionsPath =
        Get-MSBuildPropertyReference '_ActualMSBuildUserExtensionsPath'
    $actualMSBuildUserExtensionsPathUpper =
        Get-MSBuildPropertyReference '_ActualMSBuildUserExtensionsPathUpper'
    $expectedPropertyRecords = @(
        (New-GraphPropertyRecord -Name '_ExpectedRestoreConfigFile' `
            -Body (New-GetFullPathExpression `
                ($msbuildThisFileDirectory + 'NuGet.Offline.config')))
        (New-GraphPropertyRecord -Name '_ExpectedRestorePackagesPath' `
            -Body (New-GetFullPathExpression `
                ($msbuildThisFileDirectory + '..\.packages-rc48-r3')))
        (New-GraphPropertyRecord -Name '_ExpectedRestoreSources' `
            -Body (New-GetFullPathExpression `
                ($msbuildThisFileDirectory + '.offline-feed')))
        (New-GraphPropertyRecord -Name '_ExpectedMSBuildSDKsPath' `
            -Body (New-GetFullPathExpression `
                'C:\Program Files\dotnet\sdk\8.0.421\Sdks'))
        (New-GraphPropertyRecord -Name '_ExpectedMSBuildExtensionsPath' `
            -Body (New-GetFullPathExpression `
                'C:\Program Files\dotnet\sdk\8.0.421'))
        (New-GraphPropertyRecord -Name '_ExpectedDirectoryBuildPropsPath' `
            -Body (New-GetFullPathExpression `
                ($msbuildThisFileDirectory + '..\Directory.Build.props')))
        (New-GraphPropertyRecord -Name '_ExpectedTestResultsPathPrefix' `
            -Body (New-GetFullPathExpression `
                ($msbuildThisFileDirectory + '..\TestResults\')))
        (New-GraphPropertyRecord `
            -Name '_ExpectedTestResultsPathPrefixUpper' `
            -Body (New-CopyUpperExpression `
                $expectedTestResultsPathPrefix))
        (New-GraphPropertyRecord -Name '_ActualRestoreConfigFile' -Body '')
        (New-GraphPropertyRecord -Name '_ActualRestorePackagesPath' -Body '')
        (New-GraphPropertyRecord -Name '_ActualRestoreSources' -Body '')
        (New-GraphPropertyRecord -Name '_ActualNuGetPackages' -Body '')
        (New-GraphPropertyRecord -Name '_ActualMSBuildSDKsPath' -Body '')
        (New-GraphPropertyRecord -Name '_ActualMSBuildExtensionsPath' -Body '')
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsPath' -Body '')
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsPathUpper' -Body '')
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsLeaf' -Body '')
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsUnderTestResults' `
            -Body 'false')
        (New-GraphPropertyRecord `
            -Name '_ActualDirectoryBuildPropsPath' -Body '')
        (New-GraphPropertyRecord -Name '_ActualRestoreConfigFile' `
            -Condition (New-NonEmptyPropertyCondition 'RestoreConfigFile') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'RestoreConfigFile')))
        (New-GraphPropertyRecord -Name '_ActualRestorePackagesPath' `
            -Condition (New-NonEmptyPropertyCondition 'RestorePackagesPath') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'RestorePackagesPath')))
        (New-GraphPropertyRecord -Name '_ActualRestoreSources' `
            -Condition (New-NonEmptyPropertyCondition 'RestoreSources') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'RestoreSources')))
        (New-GraphPropertyRecord -Name '_ActualNuGetPackages' `
            -Condition (New-NonEmptyPropertyCondition 'NUGET_PACKAGES') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'NUGET_PACKAGES')))
        (New-GraphPropertyRecord -Name '_ActualMSBuildSDKsPath' `
            -Condition (New-NonEmptyPropertyCondition 'MSBuildSDKsPath') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'MSBuildSDKsPath')))
        (New-GraphPropertyRecord -Name '_ActualMSBuildExtensionsPath' `
            -Condition (New-NonEmptyPropertyCondition `
                'MSBuildExtensionsPath') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'MSBuildExtensionsPath')))
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsPath' `
            -Condition (New-NonEmptyPropertyCondition `
                'MSBuildUserExtensionsPath') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'MSBuildUserExtensionsPath')))
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsPathUpper' `
            -Condition (New-NonEmptyPropertyCondition `
                '_ActualMSBuildUserExtensionsPath') `
            -Body (New-CopyUpperExpression `
                $actualMSBuildUserExtensionsPath))
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsLeaf' `
            -Condition (New-NonEmptyPropertyCondition `
                '_ActualMSBuildUserExtensionsPath') `
            -Body (New-GetFileNameExpression `
                $actualMSBuildUserExtensionsPath))
        (New-GraphPropertyRecord `
            -Name '_ActualMSBuildUserExtensionsUnderTestResults' `
            -Condition (New-NonEmptyPropertyCondition `
                '_ActualMSBuildUserExtensionsPathUpper') `
            -Body (New-StartsWithExpression `
                -Value $actualMSBuildUserExtensionsPathUpper `
                -Prefix $expectedTestResultsPathPrefixUpper))
        (New-GraphPropertyRecord `
            -Name '_ActualDirectoryBuildPropsPath' `
            -Condition (New-NonEmptyPropertyCondition `
                'DirectoryBuildPropsPath') `
            -Body (New-GetFullPathExpression `
                (Get-MSBuildPropertyReference 'DirectoryBuildPropsPath')))
    )
    Assert-True -Condition (
        $propertyElements.Count -eq $expectedPropertyRecords.Count) `
        -Message "$Label PropertyGroup definition count drifted"
    for ($propertyIndex = 0;
        $propertyIndex -lt $expectedPropertyRecords.Count;
        $propertyIndex++) {
        $propertyElement = $propertyElements[$propertyIndex]
        $expectedRecord = $expectedPropertyRecords[$propertyIndex]
        Assert-True -Condition (
            $propertyElement.LocalName -ceq $expectedRecord.Name) `
            -Message "$Label PropertyGroup name/order drifted at index $propertyIndex"
        if ($expectedRecord.HasCondition) {
            Assert-ExactAttributes -Element $propertyElement `
                -ExpectedNames @('Condition') `
                -Label "$Label property $propertyIndex"
            Assert-True -Condition (
                $propertyElement.GetAttribute('Condition') -ceq
                    $expectedRecord.Condition) `
                -Message "$Label property Condition drifted at index $propertyIndex"
        }
        else {
            Assert-ExactAttributes -Element $propertyElement `
                -ExpectedNames @() -Label "$Label property $propertyIndex"
            Assert-True -Condition (
                -not $propertyElement.HasAttribute('Condition')) `
                -Message "$Label property gained Condition at index $propertyIndex"
        }
        Assert-TextOnlyElement -Element $propertyElement `
            -Label "$Label property $propertyIndex"
        Assert-True -Condition (
            [string]$propertyElement.InnerText -ceq
                [string]$expectedRecord.Body) `
            -Message "$Label property body drifted at index $propertyIndex"
    }

    $itemGroup = $projectChildren[1]
    Assert-ExactAttributes -Element $itemGroup -ExpectedNames @() `
        -Label "$Label ItemGroup"
    $items = @(Get-StrictElementChildren -Parent $itemGroup `
            -Label "$Label ItemGroup")
    $expectedItemNames = @('PortableRestoreRoot') +
        (@('RuntimeRestoreRoot') * 24) + @('InstallerRestoreRoot')
    Assert-ExactSequence -Actual @($items | ForEach-Object LocalName) `
        -Expected $expectedItemNames -Label "$Label restore root kinds"
    $expectedIncludes = @(
        '$(MSBuildThisFileDirectory)..\SolidWorksSearch.sln',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.App\SwSearch.App.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.Broker\SwSearch.Broker.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.Contracts\SwSearch.Contracts.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.Everything\SwSearch.Everything.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.Indexer\SwSearch.Indexer.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.InstallerCoordinator\SwSearch.InstallerCoordinator.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.Integration\SwSearch.Integration.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.Metadata.Worker\SwSearch.Metadata.Worker.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.PropertyConfig\SwSearch.PropertyConfig.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.PropertyViewer\SwSearch.PropertyViewer.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.ShellRegistration\SwSearch.ShellRegistration.csproj',
        '$(MSBuildThisFileDirectory)..\src\SwSearch.WebUi\SwSearch.WebUi.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.App.Tests\SwSearch.App.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.CandidateIlGate\SwSearch.CandidateIlGate.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.Everything.Tests\SwSearch.Everything.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.Indexer.Tests\SwSearch.Indexer.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.InstallerCoordinator.NativeHelper\SwSearch.InstallerCoordinator.NativeHelper.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.InstallerCoordinator.Tests\SwSearch.InstallerCoordinator.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.InstallerCoordinator.WatchdogHelper\SwSearch.InstallerCoordinator.WatchdogHelper.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.Integration.Tests\SwSearch.Integration.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.Metadata.Worker.Tests\SwSearch.Metadata.Worker.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.PropertyConfig.Tests\SwSearch.PropertyConfig.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.PropertyViewer.Tests\SwSearch.PropertyViewer.Tests.csproj',
        '$(MSBuildThisFileDirectory)..\tests\SwSearch.ShellRegistration.Tests\SwSearch.ShellRegistration.Tests.csproj',
        '$(MSBuildThisFileDirectory)wix\SwSearch.Package.wixproj'
    )
    for ($index = 0; $index -lt $items.Count; $index++) {
        Assert-ExactAttributes -Element $items[$index] `
            -ExpectedNames @('Include') -Label "$Label restore root $index"
        Assert-LeafElement -Element $items[$index] `
            -Label "$Label restore root $index"
        Assert-True -Condition (
            $items[$index].GetAttribute('Include') -ceq $expectedIncludes[$index]) `
            -Message "$Label restore root $index drifted"
    }

    $validateTarget = $projectChildren[2]
    Assert-ExactAttributes -Element $validateTarget -ExpectedNames @('Name') `
        -Label "$Label ValidateRestoreBoundaries"
    Assert-True -Condition (
        $validateTarget.GetAttribute('Name') -ceq 'ValidateRestoreBoundaries') `
        -Message "$Label validation target drifted"

    $expectedConditions = [Collections.Generic.List[string]]::new()
    [void]$expectedConditions.Add((New-CanonicalPathCondition `
                -Source 'RestoreConfigFile' -Actual '_ActualRestoreConfigFile' `
                -Expected '_ExpectedRestoreConfigFile'))
    [void]$expectedConditions.Add((New-CanonicalPathCondition `
                -Source 'RestorePackagesPath' -Actual '_ActualRestorePackagesPath' `
                -Expected '_ExpectedRestorePackagesPath'))
    [void]$expectedConditions.Add((New-CanonicalPathCondition `
                -Source 'RestoreSources' -Actual '_ActualRestoreSources' `
                -Expected '_ExpectedRestoreSources'))
    [void]$expectedConditions.Add((New-CanonicalPathCondition `
                -Source 'NUGET_PACKAGES' -Actual '_ActualNuGetPackages' `
                -Expected '_ExpectedRestorePackagesPath'))
    [void]$expectedConditions.Add((New-CanonicalPathCondition `
                -Source 'MSBuildSDKsPath' -Actual '_ActualMSBuildSDKsPath' `
                -Expected '_ExpectedMSBuildSDKsPath'))
    [void]$expectedConditions.Add((New-CanonicalPathCondition `
                -Source 'MSBuildExtensionsPath' `
                -Actual '_ActualMSBuildExtensionsPath' `
                -Expected '_ExpectedMSBuildExtensionsPath'))
    $actualUserExtensions =
        Get-MSBuildPropertyReference '_ActualMSBuildUserExtensionsPath'
    $userExtensions = Get-MSBuildPropertyReference 'MSBuildUserExtensionsPath'
    $underTestResults =
        Get-MSBuildPropertyReference '_ActualMSBuildUserExtensionsUnderTestResults'
    $userExtensionsLeaf =
        Get-MSBuildPropertyReference '_ActualMSBuildUserExtensionsLeaf'
    [void]$expectedConditions.Add(
        "'$actualUserExtensions' == '' Or '$userExtensions' != '$actualUserExtensions' Or '$underTestResults' != 'true' Or '$userExtensionsLeaf' != 'msbuild-user-extensions-empty' Or !Exists('$actualUserExtensions')")
    [void]$expectedConditions.Add((New-CanonicalPathCondition `
                -Source 'DirectoryBuildPropsPath' `
                -Actual '_ActualDirectoryBuildPropsPath' `
                -Expected '_ExpectedDirectoryBuildPropsPath'))
    foreach ($propertyName in @(
            'DirectoryBuildTargetsPath',
            'DirectoryPackagesPropsPath')) {
        [void]$expectedConditions.Add(
            (New-NotEqualCondition -Name $propertyName -Expected ''))
    }
    foreach ($propertyName in @(
            'ImportDirectoryBuildTargets',
            'ImportDirectoryPackagesProps',
            'ImportProjectExtensionProps',
            'ImportProjectExtensionTargets',
            'ImportUserLocationsByWildcardBeforeMicrosoftCommonProps',
            'ImportUserLocationsByWildcardAfterMicrosoftCommonProps',
            'ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets',
            'ImportUserLocationsByWildcardAfterMicrosoftCommonTargets',
            'ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets',
            'ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets')) {
        [void]$expectedConditions.Add(
            (New-NotEqualCondition -Name $propertyName -Expected 'false'))
    }
    [void]$expectedConditions.Add((New-NotEqualCondition `
                -Name 'ExcludeRestorePackageImports' -Expected 'true'))
    [void]$expectedConditions.Add((New-NotEqualCondition `
                -Name 'MSBuildEnableWorkloadResolver' -Expected 'false'))
    foreach ($propertyName in @(
            'RestoreNoCache',
            'RestoreDisableParallel',
            'RestoreForceEvaluate')) {
        [void]$expectedConditions.Add(
            (New-NotEqualCondition -Name $propertyName -Expected 'true'))
    }
    [void]$expectedConditions.Add((New-NotEqualCondition `
                -Name 'NuGetAudit' -Expected 'false'))
    [void]$expectedConditions.Add((New-NotEqualCondition `
                -Name 'RestoreIgnoreFailedSources' -Expected 'false'))
    [void]$expectedConditions.Add((New-NotEqualCondition `
                -Name 'DisableImplicitNuGetFallbackFolder' -Expected 'true'))
    [void]$expectedConditions.Add(
        "Exists('" +
        (Get-MSBuildPropertyReference '_ExpectedRestorePackagesPath') +
        "')")
    foreach ($propertyName in @(
            'RestoreAdditionalProjectSources',
            'RestoreFallbackFolders',
            'RestoreAdditionalProjectFallbackFolders',
            'RestoreAdditionalProjectFallbackFoldersExcludes',
            'RestoreRepositoryPath',
            'RestoreRepositoryPathOverride',
            'RestoreRootConfigDirectory',
            '_RestoreSourcesOverride',
            '_RestoreFallbackFoldersOverride',
            '_RestorePackagesPathOverride',
            '_RestoreRepositoryPathOverride')) {
        [void]$expectedConditions.Add(
            (New-NotEqualCondition -Name $propertyName -Expected ''))
    }

    $errors = @(Get-StrictElementChildren -Parent $validateTarget `
            -Label "$Label ValidateRestoreBoundaries")
    Assert-ExactSequence -Actual @($errors | ForEach-Object LocalName) `
        -Expected (@('Error') * $expectedConditions.Count) `
        -Label "$Label validation tasks"
    $actualConditions = [Collections.Generic.List[string]]::new()
    foreach ($error in $errors) {
        Assert-ExactAttributes -Element $error `
            -ExpectedNames @('Condition', 'Text') `
            -Label "$Label validation Error"
        Assert-LeafElement -Element $error -Label "$Label validation Error"
        Assert-True -Condition (
            -not [String]::IsNullOrWhiteSpace($error.GetAttribute('Text'))) `
            -Message "$Label validation Error text is empty"
        [void]$actualConditions.Add($error.GetAttribute('Condition'))
    }
    Assert-ExactSequence -Actual @($actualConditions) `
        -Expected @($expectedConditions) -Label "$Label validation conditions"

    $restoreTarget = $projectChildren[3]
    Assert-ExactAttributes -Element $restoreTarget `
        -ExpectedNames @('Name', 'DependsOnTargets') `
        -Label "$Label Restore"
    Assert-True -Condition (
        $restoreTarget.GetAttribute('Name') -ceq 'Restore' -and
        $restoreTarget.GetAttribute('DependsOnTargets') -ceq
            'ValidateRestoreBoundaries') `
        -Message "$Label Restore target boundary dependency drifted"

    $calls = @(Get-StrictElementChildren -Parent $restoreTarget `
            -Label "$Label Restore")
    Assert-ExactSequence -Actual @($calls | ForEach-Object LocalName) `
        -Expected @('MSBuild', 'MSBuild', 'MSBuild') `
        -Label "$Label Restore direct tasks"
    Assert-True -Condition (
        @($document.SelectNodes('//MSBuild')).Count -eq 3) `
        -Message "$Label must contain exactly three total MSBuild nodes"

    $expectedProjects = @(
        '@(PortableRestoreRoot)',
        '@(RuntimeRestoreRoot)',
        '@(InstallerRestoreRoot)'
    )
    $expectedRemovedProperties = @(
        'RuntimeIdentifier',
        'RuntimeIdentifiers',
        'PublishRuntimeIdentifier',
        'SelfContained',
        'SwSearchFrozenRuntimeGraph',
        'RestoreConfigFile',
        'RestorePackagesPath',
        'RestoreSources',
        'NUGET_PACKAGES',
        'MSBuildSDKsPath',
        'MSBuildExtensionsPath',
        'MSBuildUserExtensionsPath',
        'DirectoryBuildPropsPath',
        'DirectoryBuildTargetsPath',
        'DirectoryPackagesPropsPath',
        'ImportDirectoryBuildTargets',
        'ImportDirectoryPackagesProps',
        'ImportProjectExtensionProps',
        'ImportProjectExtensionTargets',
        'ImportUserLocationsByWildcardBeforeMicrosoftCommonProps',
        'ImportUserLocationsByWildcardAfterMicrosoftCommonProps',
        'ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets',
        'ImportUserLocationsByWildcardAfterMicrosoftCommonTargets',
        'ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets',
        'ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets',
        'ExcludeRestorePackageImports',
        'MSBuildEnableWorkloadResolver',
        'RestoreNoCache',
        'RestoreDisableParallel',
        'RestoreForceEvaluate',
        'NuGetAudit',
        'RestoreIgnoreFailedSources',
        'RestoreAdditionalProjectSources',
        'RestoreFallbackFolders',
        'RestoreAdditionalProjectFallbackFolders',
        'RestoreAdditionalProjectFallbackFoldersExcludes',
        'RestoreRepositoryPath',
        'RestoreRepositoryPathOverride',
        'RestoreRootConfigDirectory',
        '_RestoreSourcesOverride',
        '_RestoreFallbackFoldersOverride',
        '_RestorePackagesPathOverride',
        '_RestoreRepositoryPathOverride',
        'DisableImplicitNuGetFallbackFolder'
    )
    $runtimeForwardedProperties = [ordered]@{
        RuntimeIdentifier = 'win-x64'
        SelfContained = 'false'
        SwSearchFrozenRuntimeGraph = 'true'
    }
    $runtimeForwardedPropertyNames = @($runtimeForwardedProperties.Keys)
    $commonProperties = [ordered]@{
        RestoreConfigFile = '$(RestoreConfigFile)'
        RestorePackagesPath = '$(RestorePackagesPath)'
        RestoreSources = '$(RestoreSources)'
        NUGET_PACKAGES = '$(NUGET_PACKAGES)'
        MSBuildSDKsPath = '$(MSBuildSDKsPath)'
        MSBuildExtensionsPath = '$(MSBuildExtensionsPath)'
        MSBuildUserExtensionsPath = '$(MSBuildUserExtensionsPath)'
        DirectoryBuildPropsPath = '$(DirectoryBuildPropsPath)'
        DirectoryBuildTargetsPath = ''
        DirectoryPackagesPropsPath = ''
        ImportDirectoryBuildTargets = 'false'
        ImportDirectoryPackagesProps = 'false'
        ImportProjectExtensionProps = 'false'
        ImportProjectExtensionTargets = 'false'
        ImportUserLocationsByWildcardBeforeMicrosoftCommonProps = 'false'
        ImportUserLocationsByWildcardAfterMicrosoftCommonProps = 'false'
        ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets = 'false'
        ImportUserLocationsByWildcardAfterMicrosoftCommonTargets = 'false'
        ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets = 'false'
        ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets = 'false'
        ExcludeRestorePackageImports = 'true'
        MSBuildEnableWorkloadResolver = 'false'
        RestoreNoCache = 'true'
        RestoreDisableParallel = 'true'
        RestoreForceEvaluate = 'true'
        NuGetAudit = 'false'
        RestoreIgnoreFailedSources = 'false'
        RestoreAdditionalProjectSources = ''
        RestoreFallbackFolders = ''
        RestoreAdditionalProjectFallbackFolders = ''
        RestoreAdditionalProjectFallbackFoldersExcludes = ''
        RestoreRepositoryPath = ''
        RestoreRepositoryPathOverride = ''
        RestoreRootConfigDirectory = ''
        _RestoreSourcesOverride = ''
        _RestoreFallbackFoldersOverride = ''
        _RestorePackagesPathOverride = ''
        _RestoreRepositoryPathOverride = ''
        DisableImplicitNuGetFallbackFolder = 'true'
    }

    for ($index = 0; $index -lt $calls.Count; $index++) {
        $call = $calls[$index]
        Assert-ExactAttributes -Element $call -ExpectedNames @(
            'Projects',
            'Targets',
            'BuildInParallel',
            'RemoveProperties',
            'Properties'
        ) -Label "$Label MSBuild $index"
        Assert-LeafElement -Element $call -Label "$Label MSBuild $index"
        Assert-True -Condition (
            $call.GetAttribute('Projects') -ceq $expectedProjects[$index] -and
            $call.GetAttribute('Targets') -ceq 'Restore' -and
            $call.GetAttribute('BuildInParallel') -ceq 'false') `
            -Message "$Label MSBuild $index invocation drifted"

        $removedProperties = @(ConvertTo-ExactSemicolonList `
                -Value $call.GetAttribute('RemoveProperties') `
                -Label "$Label MSBuild $index RemoveProperties")
        $expectedRemovedPropertiesForCall = @($expectedRemovedProperties)
        if ($index -eq 1) {
            $expectedRemovedPropertiesForCall = @(
                $expectedRemovedProperties | Where-Object {
                    $runtimeForwardedPropertyNames -cnotcontains $_
                })
        }
        Assert-ExactSequence -Actual $removedProperties `
            -Expected $expectedRemovedPropertiesForCall `
            -Label "$Label MSBuild $index RemoveProperties"

        $parsedProperties = ConvertTo-ExactPropertyMap `
            -Value $call.GetAttribute('Properties') `
            -Label "$Label MSBuild $index Properties"
        $expectedProperties = [ordered]@{}
        foreach ($entry in $commonProperties.GetEnumerator()) {
            $expectedProperties[$entry.Key] = $entry.Value
        }
        if ($index -eq 1) {
            foreach ($entry in $runtimeForwardedProperties.GetEnumerator()) {
                $expectedProperties[$entry.Key] = $entry.Value
            }
        }
        else {
            $expectedProperties.RuntimeIdentifier = ''
            $expectedProperties.SelfContained = ''
            $expectedProperties.SwSearchFrozenRuntimeGraph = ''
        }
        Assert-ExactSequence -Actual @($parsedProperties.Keys) `
            -Expected @($expectedProperties.Keys) `
            -Label "$Label MSBuild $index Properties keys"
        Assert-True -Condition (
            $parsedProperties.Map.Count -eq $expectedProperties.Count) `
            -Message "$Label MSBuild $index Properties count drifted"
        foreach ($entry in $expectedProperties.GetEnumerator()) {
            Assert-True -Condition (
                $parsedProperties.Map.ContainsKey($entry.Key) -and
                [string]$parsedProperties.Map[$entry.Key] -ceq
                    [string]$entry.Value) `
                -Message "$Label MSBuild $index property drifted: $($entry.Key)"
        }
        if ($index -eq 1) {
            $invalidRuntimeForwarding = @(
                $runtimeForwardedProperties.GetEnumerator() | Where-Object {
                    $runtimePropertyName = [string]$_.Key
                    ($removedProperties -ccontains $runtimePropertyName) -or
                    (-not $parsedProperties.Map.ContainsKey($runtimePropertyName)) -or
                    ([string]$parsedProperties.Map[$runtimePropertyName] -cne
                        [string]$_.Value)
                })
            Assert-True -Condition ($invalidRuntimeForwarding.Count -eq 0) `
                -Message "$Label RuntimeRestoreRoot forwarding overlaps RemoveProperties or drifted"
        }
    }
}

function Assert-ConfigTextRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$ExpectedSource,
        [Parameter(Mandatory = $true)][string]$ExpectedPackages,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $rejected = $false
    try {
        Assert-NuGetConfigText -Text $Text -ExpectedSource $ExpectedSource `
            -ExpectedPackages $ExpectedPackages -Label "mutation $Label"
    }
    catch {
        $rejected = $true
    }
    Assert-True -Condition $rejected `
        -Message "unsafe NuGet mutation was accepted: $Label"
    $script:configMutationCount++
}

function Assert-GraphTextRejected {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $rejected = $false
    try {
        Assert-RestoreGraphText -Text $Text -Label "mutation $Label"
    }
    catch {
        $rejected = $true
    }
    Assert-True -Condition $rejected `
        -Message "unsafe RestoreGraph mutation was accepted: $Label"
    $script:graphMutationCount++
}

function Invoke-ConfigMutationFixture {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlDocument]$BaseDocument,
        [Parameter(Mandatory = $true)][scriptblock]$Mutator,
        [Parameter(Mandatory = $true)][string]$ExpectedSource,
        [Parameter(Mandatory = $true)][string]$ExpectedPackages,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $document = [Xml.XmlDocument]$BaseDocument.CloneNode($true)
    $before = $document.OuterXml
    [void](& $Mutator $document)
    $after = $document.OuterXml
    Assert-True -Condition ($after -cne $before) `
        -Message "NuGet mutation fixture made no change: $Label"
    Assert-ConfigTextRejected -Text $after -ExpectedSource $ExpectedSource `
        -ExpectedPackages $ExpectedPackages -Label $Label
}

function Invoke-GraphMutationFixture {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlDocument]$BaseDocument,
        [Parameter(Mandatory = $true)][scriptblock]$Mutator,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $document = [Xml.XmlDocument]$BaseDocument.CloneNode($true)
    $before = $document.OuterXml
    [void](& $Mutator $document)
    $after = $document.OuterXml
    Assert-True -Condition ($after -cne $before) `
        -Message "RestoreGraph mutation fixture made no change: $Label"
    Assert-GraphTextRejected -Text $after -Label $Label
}

$offlineConfigText = Read-WorkspaceText 'installer/NuGet.Offline.config'
$resolverConfigText = Read-WorkspaceText 'NuGet.config'
$restoreGraphText = Read-WorkspaceText 'installer/RestoreGraph.proj'

Assert-NuGetConfigText -Text $offlineConfigText `
    -ExpectedSource '.offline-feed' `
    -ExpectedPackages '..\.packages-rc48-r3' `
    -Label 'installer/NuGet.Offline.config'
Assert-NuGetConfigText -Text $resolverConfigText `
    -ExpectedSource 'installer\.offline-feed' `
    -ExpectedPackages '.packages-rc48-r3' `
    -Label 'NuGet.config'
Assert-RestoreGraphText -Text $restoreGraphText `
    -Label 'installer/RestoreGraph.proj'

$offlineDocument = ConvertFrom-SafeXmlText -Text $offlineConfigText `
    -Label 'offline mutation baseline'
$resolverDocument = ConvertFrom-SafeXmlText -Text $resolverConfigText `
    -Label 'resolver mutation baseline'
$graphDocument = ConvertFrom-SafeXmlText -Text $restoreGraphText `
    -Label 'graph mutation baseline'

# Confirm compact DOM serialization remains an accepted baseline before mutating it.
Assert-NuGetConfigText -Text $offlineDocument.OuterXml `
    -ExpectedSource '.offline-feed' `
    -ExpectedPackages '..\.packages-rc48-r3' `
    -Label 'offline compact baseline'
Assert-NuGetConfigText -Text $resolverDocument.OuterXml `
    -ExpectedSource 'installer\.offline-feed' `
    -ExpectedPackages '.packages-rc48-r3' `
    -Label 'resolver compact baseline'
Assert-RestoreGraphText -Text $graphDocument.OuterXml `
    -Label 'graph compact baseline'

$offlineFixture = @{
    BaseDocument = $offlineDocument
    ExpectedSource = '.offline-feed'
    ExpectedPackages = '..\.packages-rc48-r3'
}

Invoke-ConfigMutationFixture @offlineFixture -Label 'source URI' -Mutator {
    param($document)
    $document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute(
        'value', 'https://example.invalid/v3/index.json')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source URI credentials' -Mutator {
    param($document)
    $document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute(
        'value', 'https://user:password@example.invalid/v3/index.json')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source UNC' -Mutator {
    param($document)
    $document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute(
        'value', '\\server\share\feed')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source absolute' -Mutator {
    param($document)
    $document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute(
        'value', 'C:\untrusted-feed')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source MSBuild variable' -Mutator {
    param($document)
    $document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute(
        'value', '$(UserProfile)\feed')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source environment variable' -Mutator {
    param($document)
    $document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute(
        'value', '%USERPROFILE%\feed')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source key' -Mutator {
    param($document)
    $document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute(
        'key', 'UnapprovedFeed')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source clear missing' -Mutator {
    param($document)
    $clear = $document.SelectSingleNode('/configuration/packageSources/clear')
    [void]$clear.ParentNode.RemoveChild($clear)
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source order' -Mutator {
    param($document)
    $parent = $document.SelectSingleNode('/configuration/packageSources')
    $add = $parent.SelectSingleNode('add')
    $clear = $parent.SelectSingleNode('clear')
    [void]$parent.InsertBefore($add, $clear)
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'extra source' -Mutator {
    param($document)
    $parent = $document.SelectSingleNode('/configuration/packageSources')
    $add = $document.CreateElement('add')
    [void]$add.SetAttribute('key', 'SecondFeed')
    [void]$add.SetAttribute('value', '.second-feed')
    [void]$parent.AppendChild($add)
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'source extra attribute' -Mutator {
    param($document)
    [void]$document.SelectSingleNode(
        '/configuration/packageSources/add').SetAttribute('enabled', 'true')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'fallback entry' -Mutator {
    param($document)
    $parent = $document.SelectSingleNode(
        '/configuration/fallbackPackageFolders')
    $add = $document.CreateElement('add')
    [void]$add.SetAttribute('key', 'Fallback')
    [void]$add.SetAttribute('value', 'C:\untrusted-fallback')
    [void]$parent.AppendChild($add)
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'extra config key' -Mutator {
    param($document)
    $parent = $document.SelectSingleNode('/configuration/config')
    $add = $document.CreateElement('add')
    [void]$add.SetAttribute('key', 'repositoryPath')
    [void]$add.SetAttribute('value', '..\packages')
    [void]$parent.AppendChild($add)
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'config order' -Mutator {
    param($document)
    $parent = $document.SelectSingleNode('/configuration/config')
    $audit = $parent.SelectSingleNode('add[@key="auditMode"]')
    $packages = $parent.SelectSingleNode('add[@key="globalPackagesFolder"]')
    [void]$parent.InsertBefore($audit, $packages)
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'wrong package folder' -Mutator {
    param($document)
    [void]$document.SelectSingleNode(
        '/configuration/config/add[@key="globalPackagesFolder"]').SetAttribute(
        'value', '..\.packages-rc48-r2')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'wrong audit mode' -Mutator {
    param($document)
    [void]$document.SelectSingleNode(
        '/configuration/config/add[@key="auditMode"]').SetAttribute(
        'value', 'all')
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'credential section' -Mutator {
    param($document)
    $credentials = $document.CreateElement('packageSourceCredentials')
    [void]$document.DocumentElement.AppendChild($credentials)
}
Invoke-ConfigMutationFixture @offlineFixture -Label 'extra root comment' -Mutator {
    param($document)
    [void]$document.DocumentElement.AppendChild(
        $document.CreateComment('unapproved'))
}

$configNamespaceMutation = $offlineConfigText.Replace(
    '<configuration>',
    '<configuration xmlns="urn:unapproved">')
Assert-True -Condition ($configNamespaceMutation -cne $offlineConfigText) `
    -Message 'config namespace fixture made no change'
Assert-ConfigTextRejected -Text $configNamespaceMutation `
    -ExpectedSource '.offline-feed' `
    -ExpectedPackages '..\.packages-rc48-r3' `
    -Label 'default namespace'

$configBody = [regex]::Replace(
    $offlineConfigText,
    '^\s*<\?xml[^?]*\?>\s*',
    '',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
$configDtdMutation =
    '<?xml version="1.0" encoding="utf-8"?>' +
    '<!DOCTYPE configuration [<!ENTITY injected "value">]>' +
    $configBody
Assert-ConfigTextRejected -Text $configDtdMutation `
    -ExpectedSource '.offline-feed' `
    -ExpectedPackages '..\.packages-rc48-r3' `
    -Label 'DTD'

Invoke-ConfigMutationFixture `
    -BaseDocument $resolverDocument `
    -ExpectedSource 'installer\.offline-feed' `
    -ExpectedPackages '.packages-rc48-r3' `
    -Label 'cross-config source' `
    -Mutator {
        param($document)
        [void]$document.SelectSingleNode(
            '/configuration/packageSources/add').SetAttribute(
            'value', '.offline-feed')
    }

Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'extra direct MSBuild' -Mutator {
        param($document)
        $restore = $document.SelectSingleNode('/Project/Target[@Name="Restore"]')
        $copy = $restore.SelectNodes('MSBuild')[2].CloneNode($true)
        [void]$restore.AppendChild($copy)
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'nested MSBuild' -Mutator {
        param($document)
        $restore = $document.SelectSingleNode('/Project/Target[@Name="Restore"]')
        $calls = @($restore.SelectNodes('MSBuild'))
        [void]$calls[0].AppendChild($calls[2].CloneNode($true))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'missing runtime MSBuild' -Mutator {
        param($document)
        $restore = $document.SelectSingleNode('/Project/Target[@Name="Restore"]')
        $call = $restore.SelectNodes('MSBuild')[1]
        [void]$restore.RemoveChild($call)
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'MSBuild order' -Mutator {
        param($document)
        $restore = $document.SelectSingleNode('/Project/Target[@Name="Restore"]')
        $first = $restore.SelectNodes('MSBuild')[0]
        [void]$restore.AppendChild($first)
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'MSBuild project root' -Mutator {
        param($document)
        [void]$document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0].SetAttribute(
            'Projects', '@(RuntimeRestoreRoot)')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'MSBuild extra attribute' -Mutator {
        param($document)
        [void]$document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0].SetAttribute(
            'ContinueOnError', 'true')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'RemoveProperties missing' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        $parts = @($call.GetAttribute('RemoveProperties').Split(';') |
                Where-Object { $_ -cne 'RestoreForceEvaluate' })
        [void]$call.SetAttribute('RemoveProperties', ($parts -join ';'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'RemoveProperties duplicate' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        [void]$call.SetAttribute(
            'RemoveProperties',
            $call.GetAttribute('RemoveProperties') + ';RestoreForceEvaluate')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'RemoveProperties extra' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        [void]$call.SetAttribute(
            'RemoveProperties',
            $call.GetAttribute('RemoveProperties') + ';UnapprovedProperty')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'runtime forwarding overlap' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[1]
        [void]$call.SetAttribute(
            'RemoveProperties',
            $call.GetAttribute('RemoveProperties') + ';RuntimeIdentifier')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'Properties missing' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        $parts = @($call.GetAttribute('Properties').Split(';') |
                Where-Object { $_.Trim() -notlike 'RestoreForceEvaluate=*' })
        [void]$call.SetAttribute('Properties', ($parts -join ';'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'Properties duplicate' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties') + ';RestoreForceEvaluate=true')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'Properties extra' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties') + ';UnapprovedProperty=true')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'Properties unsafe value' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties').Replace(
                'RestoreForceEvaluate=true',
                'RestoreForceEvaluate=false'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'portable runtime identifier' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[0]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties').Replace(
                'RuntimeIdentifier=;',
                'RuntimeIdentifier=linux-x64;'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'runtime identifier' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[1]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties').Replace(
                'RuntimeIdentifier=win-x64',
                'RuntimeIdentifier=linux-x64'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'runtime self contained' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[1]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties').Replace(
                'SelfContained=false',
                'SelfContained=true'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'runtime graph disabled' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[1]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties').Replace(
                'SwSearchFrozenRuntimeGraph=true',
                'SwSearchFrozenRuntimeGraph=false'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'installer runtime graph' -Mutator {
        param($document)
        $call = $document.SelectNodes(
            '/Project/Target[@Name="Restore"]/MSBuild')[2]
        [void]$call.SetAttribute(
            'Properties',
            $call.GetAttribute('Properties').Replace(
                'SwSearchFrozenRuntimeGraph=',
                'SwSearchFrozenRuntimeGraph=true'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'validation gate missing' -Mutator {
        param($document)
        $target = $document.SelectSingleNode(
            '/Project/Target[@Name="ValidateRestoreBoundaries"]')
        [void]$target.RemoveChild($target.SelectNodes('Error')[0])
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'validation gate condition' -Mutator {
        param($document)
        $error = $document.SelectSingleNode(
            '/Project/Target[@Name="ValidateRestoreBoundaries"]/Error[contains(@Condition, "RestoreForceEvaluate")]')
        [void]$error.SetAttribute(
            'Condition',
            $error.GetAttribute('Condition').Replace("'true'", "'false'"))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'validation extra task' -Mutator {
        param($document)
        $target = $document.SelectSingleNode(
            '/Project/Target[@Name="ValidateRestoreBoundaries"]')
        [void]$target.AppendChild($document.CreateElement('Message'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'Restore dependency' -Mutator {
        param($document)
        [void]$document.SelectSingleNode(
            '/Project/Target[@Name="Restore"]').SetAttribute(
            'DependsOnTargets', '')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'restore root include' -Mutator {
        param($document)
        [void]$document.SelectSingleNode(
            '/Project/ItemGroup/PortableRestoreRoot').SetAttribute(
            'Include', '$(MSBuildThisFileDirectory)..\src\SwSearch.App\SwSearch.App.csproj')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'TreatAsLocalProperty missing' -Mutator {
        param($document)
        $project = $document.DocumentElement
        $parts = @($project.GetAttribute('TreatAsLocalProperty').Split(';') |
                Where-Object { $_ -cne '_ActualRestoreSources' })
        [void]$project.SetAttribute(
            'TreatAsLocalProperty',
            ($parts -join ';'))
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'PropertyGroup positions 9 and 10 swapped' -Mutator {
        param($document)
        $propertyGroup = $document.SelectSingleNode('/Project/PropertyGroup')
        $properties = @($propertyGroup.SelectNodes('*'))
        [void]$propertyGroup.InsertBefore($properties[9], $properties[8])
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'PropertyGroup user extension containment constant true' -Mutator {
        param($document)
        $definitions = @($document.SelectNodes(
                '/Project/PropertyGroup/_ActualMSBuildUserExtensionsUnderTestResults'))
        $definitions[1].InnerText = 'true'
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'PropertyGroup unconditional property gained Condition' -Mutator {
        param($document)
        $property = $document.SelectNodes('/Project/PropertyGroup/*')[8]
        [void]$property.SetAttribute(
            'Condition',
            "'`$(RestoreConfigFile)' != ''")
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'PropertyGroup conditional property lost Condition' -Mutator {
        param($document)
        $property = $document.SelectNodes('/Project/PropertyGroup/*')[19]
        [void]$property.RemoveAttribute('Condition')
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'PropertyGroup expected expression constantized' -Mutator {
        param($document)
        $document.SelectSingleNode(
            '/Project/PropertyGroup/_ExpectedRestoreConfigFile').InnerText =
            'C:\untrusted.config'
    }
Invoke-GraphMutationFixture -BaseDocument $graphDocument `
    -Label 'Restore comment node' -Mutator {
        param($document)
        [void]$document.SelectSingleNode(
            '/Project/Target[@Name="Restore"]').AppendChild(
            $document.CreateComment('unapproved'))
    }

$graphNamespaceMutation = $restoreGraphText.Replace(
    '<Project ',
    '<Project xmlns="urn:unapproved" ')
Assert-True -Condition ($graphNamespaceMutation -cne $restoreGraphText) `
    -Message 'graph namespace fixture made no change'
Assert-GraphTextRejected -Text $graphNamespaceMutation `
    -Label 'graph default namespace'

$graphBody = [regex]::Replace(
    $restoreGraphText,
    '^\s*<\?xml[^?]*\?>\s*',
    '',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
$graphDtdMutation =
    '<!DOCTYPE Project [<!ENTITY injected "value">]>' + $graphBody
Assert-GraphTextRejected -Text $graphDtdMutation -Label 'graph DTD'

$paths = @(
    (Resolve-WorkspaceFile 'installer/RestoreGraph.proj'),
    (Resolve-WorkspaceFile 'installer/NuGet.Offline.config'),
    (Resolve-WorkspaceFile 'NuGet.config')
)
$hashes = [ordered]@{}
foreach ($path in $paths) {
    $relative = $path.Substring($root.Length + 1).Replace('\', '/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

[pscustomobject][ordered]@{
    Result = 'RC48_R3_RESTORE_GRAPH_CONFIG_STATIC_PASS'
    ConfigMutationFixturesRejected = $script:configMutationCount
    GraphMutationFixturesRejected = $script:graphMutationCount
    RestoreGraphSha256 = $hashes['installer/RestoreGraph.proj']
    OfflineConfigSha256 = $hashes['installer/NuGet.Offline.config']
    ResolverConfigSha256 = $hashes['NuGet.config']
} | ConvertTo-Json -Compress
