param([string]$Compiler)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Compiler) {
    $Compiler = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter fxc.exe -Recurse |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (!$Compiler) { throw 'Install the Windows SDK shader compiler or specify -Compiler.' }
# Keep the lens displacement evaluation stable across all four corner normals.
& $Compiler /T ps_2_0 /E main /O3 /Gis /Fo (Join-Path $projectRoot 'src/MiniC/Effects/CrystalGlass.ps') (Join-Path $projectRoot 'src/MiniC/Effects/CrystalGlass.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'Crystal shader compilation failed.' }
foreach ($stage in @(@('vs_5_0', 'vertex', 'vs'), @('ps_5_0', 'pixel', 'ps'))) {
    & $Compiler /T $stage[0] /E $stage[1] /O3 /Gis /Fo (Join-Path $projectRoot "src/MiniC/Effects/NativeCrystal.$($stage[2]).cso") (Join-Path $projectRoot 'src/MiniC/Effects/NativeCrystal.hlsl')
    if ($LASTEXITCODE -ne 0) { throw 'Native crystal shader compilation failed.' }
    & $Compiler /T $stage[0] /E $stage[1] /O3 /Gis /Fo (Join-Path $projectRoot "src/MiniC/Effects/BackdropLayer.$($stage[2]).cso") (Join-Path $projectRoot 'src/MiniC/Effects/BackdropLayer.hlsl')
    if ($LASTEXITCODE -ne 0) { throw 'Backdrop compositor shader compilation failed.' }
}
