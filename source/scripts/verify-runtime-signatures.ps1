param([Parameter(Mandatory=$true)][string]$RuntimeRoot)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $RuntimeRoot).Path
$manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
if(-not $manifest.signing -or -not $manifest.signing.thumbprint){throw 'Package has no signing identity'}
$checked=@()
foreach($entry in $manifest.signing.files){
 $file=[IO.Path]::GetFullPath((Join-Path $root $entry.file))
 if(-not $file.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Signature path outside package'}
 $signature=Get-AuthenticodeSignature -LiteralPath $file
 if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $manifest.signing.thumbprint){throw ('Invalid or different publisher: '+$entry.file)}
 $checked+=$entry.file
}
if($checked.Count -eq 0){throw 'No signature coverage'}
@{passed=$true;thumbprint=$manifest.signing.thumbprint;checked=@($checked)} | ConvertTo-Json -Depth 4 -Compress
