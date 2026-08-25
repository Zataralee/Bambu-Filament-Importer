param(
    [string]$CatalogPath = "$(Split-Path -Parent $PSScriptRoot)\catalogs\manufacturers.json",
    [string]$OutputFolder = "$(Split-Path -Parent $PSScriptRoot)\packages\manufacturers"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$usedFilamentIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

function Get-Template([string]$Family) {
    switch -Regex ($Family) {
        '^ABS$' { return 'fdm_filament_abs' }
        '^ASA$' { return 'fdm_filament_asa' }
        '^BVOH$' { return 'fdm_filament_bvoh' }
        '^HIPS$' { return 'fdm_filament_hips' }
        '^PCTG$' { return 'fdm_filament_pctg' }
        '^PETG' { return 'fdm_filament_pet' }
        '^PA' { return 'fdm_filament_pa' }
        '^PC$' { return 'fdm_filament_pc' }
        '^PP' { return 'fdm_filament_pp' }
        '^PVA$' { return 'fdm_filament_pva' }
        '^TPU$' { return 'fdm_filament_tpu' }
        default { return 'fdm_filament_pla' }
    }
}

function Get-MaterialType([string]$Family) {
    switch -Regex ($Family) {
        '^PLA' { return 'PLA' }
        '^PETG' { return 'PETG' }
        '^PA' { return 'PA' }
        '^PP' { return 'PP' }
        default { return $Family }
    }
}

function Get-DeterministicCode([string]$Text, [int]$Length) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        $hash = [Convert]::ToHexString($sha.ComputeHash($bytes))
        return $hash.Substring(0, $Length)
    }
    finally {
        $sha.Dispose()
    }
}

function Get-AmsSafeFilamentId([string]$ExistingId, [string]$StableKey) {
    $current = $ExistingId.Trim()
    if ($current.Length -gt 0 -and $current.Length -le 8 -and $usedFilamentIds.Add($current)) {
        return $current
    }

    if ($current.Length -gt 8) {
        $truncated = $current.Substring(0, 8)
        if ($usedFilamentIds.Add($truncated)) {
            return $truncated
        }
    }

    for ($salt = 0; ; $salt++) {
        $input = if ($salt -eq 0) { $StableKey } else { "$StableKey|$salt" }
        $candidate = 'V' + (Get-DeterministicCode $input 7)
        if ($usedFilamentIds.Add($candidate)) {
            return $candidate
        }
    }
}

function Get-Array([object]$Value) {
    return @([string]$Value)
}

function Write-JsonFile([string]$Path, [object]$Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null

foreach ($manufacturer in $catalog.manufacturers) {
    $packageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("bflib_catalog_" + [Guid]::NewGuid().ToString("N"))
    try {
        $vendorFolder = Join-Path $packageRoot ("filament\" + [string]$manufacturer.manufacturer)
        New-Item -ItemType Directory -Path $vendorFolder -Force | Out-Null
        $profiles = @()
        $projectPresetNames = @()

        foreach ($product in $manufacturer.products) {
            $name = [string]$product.name
            $family = [string]$product.family
            $baseName = "$name @base"
            $stableKey = "$($manufacturer.manufacturer)|$name"
            $filamentId = Get-AmsSafeFilamentId ("V" + (Get-DeterministicCode $stableKey 4)) $baseName
            $sourceUrl = if ([string]::IsNullOrWhiteSpace([string]$product.sourceUrl)) {
                [string]$manufacturer.sourceUrls[0]
            } else {
                [string]$product.sourceUrl
            }
            $description = "Manufacturer starting profile. Recommended range: $($product.nozzleLow)-$($product.nozzleHigh) C nozzle and $($product.bedLow)-$($product.bedHigh) C bed. Source: $sourceUrl"
            if ([bool]$product.abrasive) {
                $description += " A hardened nozzle and hardened extruder gears are recommended."
            }
            if ([bool]$product.enclosure) {
                $description += " An enclosed printer is recommended."
            }

            $base = [ordered]@{
                type = 'filament'
                name = $baseName
                inherits = Get-Template $family
                from = 'system'
                filament_id = $filamentId
                instantiation = 'false'
                description = $description
                filament_vendor = @([string]$manufacturer.manufacturer)
                filament_type = @(Get-MaterialType $family)
                filament_flow_ratio = Get-Array $product.flow
                filament_max_volumetric_speed = Get-Array $product.maxVolumetricSpeed
                nozzle_temperature_range_low = Get-Array $product.nozzleLow
                nozzle_temperature_range_high = Get-Array $product.nozzleHigh
                hot_plate_temp = Get-Array $product.bed
                hot_plate_temp_initial_layer = Get-Array $product.bed
                textured_plate_temp = Get-Array $product.bed
                textured_plate_temp_initial_layer = Get-Array $product.bed
                eng_plate_temp = Get-Array $product.bed
                eng_plate_temp_initial_layer = Get-Array $product.bed
                cool_plate_temp = @('0')
                cool_plate_temp_initial_layer = @('0')
                fan_min_speed = Get-Array $product.fanMin
                fan_max_speed = Get-Array $product.fanMax
            }
            if ([int]$product.dryingTemp -gt 0) {
                $base.filament_dev_ams_drying_temperature = @([string]$product.dryingTemp, [string]$product.dryingTemp, [string]$product.dryingTemp, [string]$product.dryingTemp)
                $base.filament_dev_ams_drying_time = @([string]$product.dryingHours, [string]$product.dryingHours, [string]$product.dryingHours, [string]$product.dryingHours)
            }
            if ([bool]$product.enclosure) {
                $base.chamber_temperature = @('45')
            }

            $base.nozzle_temperature = @([string]$product.nozzle, [string]$product.nozzle)
            $base.nozzle_temperature_initial_layer = @([string]$product.nozzle, [string]$product.nozzle)

            $baseFile = Join-Path $vendorFolder "$baseName.json"
            Write-JsonFile $baseFile $base

            $folderName = [string]$manufacturer.manufacturer
            $profiles += [ordered]@{
                name = $baseName
                relativePath = "filament/$folderName/$baseName.json"
                kind = 'system'
                vendorGroup = [string]$manufacturer.manufacturer
                materialFamily = $family
            }
        }

        $manifest = [ordered]@{
            format = 'bambu-filament-library'
            formatVersion = 1
            packageId = [string]$manufacturer.packageId
            displayName = [string]$manufacturer.displayName
            manufacturer = [string]$manufacturer.manufacturer
            version = [string]$catalog.version
            createdUtc = (Get-Date).ToUniversalTime().ToString('o')
            printerNeutral = $true
            compatibilityNote = 'Printer-neutral manufacturer definitions. The importer discovers configured printer models and nozzle sizes from Bambu Studio, then creates compatible per-printer presets during installation. Manufacturer recommendations are starting points; calibrate flow ratio and maximum volumetric speed for each spool and nozzle.'
            sourceUrls = @($manufacturer.sourceUrls)
            profiles = $profiles
            projectPresetNames = $projectPresetNames
        }
        Write-JsonFile (Join-Path $packageRoot 'manifest.json') $manifest

        $outputPath = Join-Path $OutputFolder ([string]$manufacturer.outputFile)
        if (Test-Path -LiteralPath $outputPath) {
            Remove-Item -LiteralPath $outputPath -Force
        }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $outputPath)
        Write-Host "Created $outputPath ($($manufacturer.products.Count) filaments, $($profiles.Count) profiles)"
    }
    finally {
        if (Test-Path -LiteralPath $packageRoot) {
            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }
    }
}

# Migrate the original curated SUNLU library to the printer-neutral package contract.
$projectRoot = Split-Path -Parent $PSScriptRoot
$sunluSource = Join-Path $projectRoot 'catalogs\source\SUNLU-legacy-0.3.bflib'
if (Test-Path -LiteralPath $sunluSource) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($sunluSource)
    $packageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("bflib_sunlu_neutral_" + [Guid]::NewGuid().ToString("N"))
    try {
        $manifestEntry = $archive.GetEntry('manifest.json')
        if ($null -eq $manifestEntry) {
            throw "The source SUNLU package has no manifest.json."
        }
        $manifestReader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try {
            $sourceManifest = $manifestReader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $manifestReader.Dispose()
        }

        $vendorFolder = Join-Path $packageRoot 'filament\SUNLU'
        New-Item -ItemType Directory -Path $vendorFolder -Force | Out-Null
        $neutralProfiles = @()
        $baseEntries = @($sourceManifest.profiles | Where-Object { ([string]$_.name).EndsWith('@base') })
        foreach ($baseEntry in $baseEntries) {
            $zipPath = ([string]$baseEntry.relativePath).Replace('\', '/')
            $profileEntry = $archive.GetEntry($zipPath)
            if ($null -eq $profileEntry) {
                throw "Missing SUNLU base profile: $zipPath"
            }
            $profileReader = [System.IO.StreamReader]::new($profileEntry.Open())
            try {
                $baseJson = $profileReader.ReadToEnd() | ConvertFrom-Json
            }
            finally {
                $profileReader.Dispose()
            }

            $baseJson.filament_id = Get-AmsSafeFilamentId ([string]$baseJson.filament_id) ([string]$baseEntry.name)

            $preferredChild = $sourceManifest.profiles | Where-Object {
                ([string]$_.name).Equals((([string]$baseEntry.name).Replace(' @base', ' @BBL X1C')))
            } | Select-Object -First 1
            if ($null -ne $preferredChild) {
                $childZipPath = ([string]$preferredChild.relativePath).Replace('\', '/')
                $childEntry = $archive.GetEntry($childZipPath)
                if ($null -ne $childEntry) {
                    $childReader = [System.IO.StreamReader]::new($childEntry.Open())
                    try {
                        $childJson = $childReader.ReadToEnd() | ConvertFrom-Json
                        foreach ($setting in @('nozzle_temperature', 'nozzle_temperature_initial_layer', 'filament_flow_ratio', 'filament_max_volumetric_speed')) {
                            if ($null -ne $childJson.$setting) {
                                $baseJson | Add-Member -NotePropertyName $setting -NotePropertyValue @($childJson.$setting) -Force
                            }
                        }
                    }
                    finally {
                        $childReader.Dispose()
                    }
                }
            }

            $fileName = [System.IO.Path]::GetFileName($zipPath)
            Write-JsonFile (Join-Path $vendorFolder $fileName) $baseJson
            $neutralProfiles += [ordered]@{
                name = [string]$baseEntry.name
                relativePath = "filament/SUNLU/$fileName"
                kind = 'system'
                vendorGroup = [string]$baseEntry.vendorGroup
                materialFamily = [string]$baseEntry.materialFamily
            }
        }

        $neutralManifest = [ordered]@{
            format = 'bambu-filament-library'
            formatVersion = 1
            packageId = 'sunlu-neutral-2026-08'
            displayName = 'SUNLU Complete Filament Library'
            manufacturer = 'SUNLU'
            version = [string]$catalog.version
            createdUtc = (Get-Date).ToUniversalTime().ToString('o')
            printerNeutral = $true
            compatibilityNote = 'Printer-neutral SUNLU definitions grouped by material family. The importer discovers configured printer models and nozzle sizes from Bambu Studio, then creates compatible per-printer presets during installation.'
            sourceUrls = @(
                'https://www.sunlu.com/collections/1?name=Filaments',
                'https://www.sunlu.com/wiki/filament-usage-guide',
                'https://www.sunlu.com/wiki/fdm-performance-comparison'
            )
            profiles = $neutralProfiles
            projectPresetNames = @()
        }
        Write-JsonFile (Join-Path $packageRoot 'manifest.json') $neutralManifest

        $outputPath = Join-Path $OutputFolder 'SUNLU.bflib'
        if (Test-Path -LiteralPath $outputPath) {
            Remove-Item -LiteralPath $outputPath -Force
        }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $outputPath)
        Write-Host "Created $outputPath ($($neutralProfiles.Count) filaments, $($neutralProfiles.Count) profiles)"
    }
    finally {
        $archive.Dispose()
        if (Test-Path -LiteralPath $packageRoot) {
            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }
    }
}
