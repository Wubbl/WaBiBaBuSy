# Build a self-contained Linux client from Windows and pack it as .tar.gz.
#   powershell -File tools\linux\publish-client.ps1 [-Rid linux-x64|linux-arm64]
#   Extra arguments go to dotnet publish (Build-Installer.ps1 passes its version overrides).
param([string]$Rid = "linux-x64", [Parameter(ValueFromRemainingArguments)][string[]]$PublishArgs = @())
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$out = Join-Path $root "publish-linux\wabibabusy-client-$Rid"  # not publish\: the Windows installer packs that whole folder

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish "$root\WaBiBaBuSy.Client.Linux\WaBiBaBuSy.Client.Linux.csproj" -c Release -r $Rid --self-contained -o $out @PublishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Windows 10+ ships bsdtar; file modes are fixed up by install.sh on the Linux side
tar -czf "$out.tar.gz" -C (Join-Path $root "publish-linux") "wabibabusy-client-$Rid"
Write-Host "Done: $out.tar.gz"
Write-Host "On each Linux machine: tar xzf wabibabusy-client-$Rid.tar.gz && bash wabibabusy-client-$Rid/packaging/install.sh"
