param([string]$RuntimeRoot,[string]$Configuration,[string[]]$FilePath)
$ErrorActionPreference='Stop'
if(-not $Configuration){$Configuration=Join-Path $PSScriptRoot '..\private\signing\capskov-local.json'}
$config=Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
if($config.schema -ne 'foundation-local-signing-v1'){throw 'Unsupported local signing configuration'}
$certificate=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$config.thumbprint)
if(-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)){throw 'Code signing private key missing or expired'}
if(-not ($certificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')){throw 'Code Signing EKU required'}
if($RuntimeRoot){
 $relative=@('plugin\Bimwright.Dwg.Plugin.Acad26.dll','plugin\Bimwright.Dwg.Plugin.Journal.dll','server\Bimwright.Dwg.Server.exe','server\Bimwright.Dwg.Server.dll','server\Bimwright.Dwg.Plugin.Journal.dll')
 $FilePath=@($relative | ForEach-Object {Join-Path $RuntimeRoot $_} | Where-Object {Test-Path -LiteralPath $_})
}
if(-not $FilePath -or $FilePath.Count -eq 0){throw 'No owned Foundation binaries selected'}
$results=@()
foreach($file in $FilePath){
 $resolved=(Resolve-Path -LiteralPath $file).Path
 $name=[IO.Path]::GetFileName($resolved)
 if($name -notmatch '^(Bimwright\.Dwg\.(Plugin\.(Acad26|Journal)|Server)|Foundation\.(SaveFontRecovery|SaveXrefCacheRecovery2|SaveXrefCacheRecovery|TextAnchorRecovery|BlockCacheRecovery2))\.(dll|exe)$'){throw ('Not an owned Foundation binary: '+$name)}
 $signature=Set-AuthenticodeSignature -LiteralPath $resolved -Certificate $certificate -HashAlgorithm SHA256 -IncludeChain NotRoot
 $checked=Get-AuthenticodeSignature -LiteralPath $resolved
 if($signature.Status -ne 'Valid' -or $checked.Status -ne 'Valid' -or $checked.SignerCertificate.Thumbprint -ne $config.thumbprint){throw ('Signature verification failed for '+$name+': '+$checked.StatusMessage)}
 $label=if($RuntimeRoot){[IO.Path]::GetRelativePath((Resolve-Path $RuntimeRoot).Path,$resolved).Replace('\','/')}else{$name}
 $results+=@{file=$label;status='Valid';sha256=(Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash}
}
@{schema='foundation-authenticode-signing-v1';subject=$certificate.Subject;thumbprint=$certificate.Thumbprint;algorithm='SHA256';self_signed=($certificate.Subject -eq $certificate.Issuer);trust_scope='CurrentUser';timestamped=$false;files=@($results)} | ConvertTo-Json -Depth 6 -Compress
