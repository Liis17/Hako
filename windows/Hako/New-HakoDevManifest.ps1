param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)

$manifest = [xml](Get-Content -LiteralPath $Source -Raw)
$namespace = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespace.AddNamespace('mp', 'http://schemas.microsoft.com/appx/2014/phone/manifest')
$namespace.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')

$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespace)
$identity.SetAttribute('Name', $identity.GetAttribute('Name') + '-dev')
$manifest.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $namespace).InnerText = 'Hako-dev'
$visualElements = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application/uap:VisualElements', $namespace)
$visualElements.SetAttribute('DisplayName', 'Hako-dev')
$visualElements.SetAttribute('Description', 'Hako-dev')

$extensions = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application/f:Extensions', $namespace)
if ($null -ne $extensions) {
    $extensions.ParentNode.RemoveChild($extensions) | Out-Null
}

$phoneIdentity = $manifest.SelectSingleNode('/f:Package/mp:PhoneIdentity', $namespace)
if ($null -ne $phoneIdentity) {
    $phoneIdentity.ParentNode.RemoveChild($phoneIdentity) | Out-Null
}

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$writer = [System.Xml.XmlWriter]::Create($Destination, $settings)
try {
    $manifest.Save($writer)
}
finally {
    $writer.Dispose()
}
