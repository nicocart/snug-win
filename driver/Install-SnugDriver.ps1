[CmdletBinding()]
param(
    [ValidateSet("Install", "Uninstall", "Status")]
    [string]$Action = "Install"
)

$ErrorActionPreference = "Stop"

$ExpectedVersion = "0.3.1"
$ExpectedZipSha256 = "B3E3A5AB9B49BD56A7E753120CDDB1D913479BC5C0DFA781C3D43392DDE2FB75"
$ExpectedMsiSha256 = "036E27CEA782B1297AB7D822CC59694105952CDE5BA885D0B2850D4005D79390"
$ExpectedCertSha256 = "D8C5783083BF892339F5ED4191BBF3BE502CBF85F19FF29EFB00568B5F8462B7"
$ExpectedThumbprint = "5DFA7E68997ED52D4B10420407860D1C29B6F427"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ZipPath = Join-Path $ScriptDir "virtual-desktop-driver-installer-x64.zip"
$InstallerDir = Join-Path $ScriptDir "installer"
$MsiPath = Join-Path $InstallerDir "virtual-display-driver-0.3.1-x86_64.msi"
$CertPath = Join-Path $InstallerDir "DriverCertificate.cer"

$StateDir = Join-Path $env:ProgramData "SnugWin"
$OwnershipPath = Join-Path $StateDir "driver-ownership.json"

function Test-ExistingDriver {
    $pipeExists = [System.IO.Directory]::GetFiles("\\.\pipe\") -contains "\\.\pipe\virtualdisplaydriver"
    $svc = Get-Service -Name "vdd-user-session-service" -ErrorAction SilentlyContinue
    $rootKey = "HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY"
    $hasDevice = $false
    if (Test-Path $rootKey) {
        foreach ($sub in Get-ChildItem $rootKey -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue
            $isCherryOrVdd = ($props.Mfg -like "*Cherry*") -or ($props.DeviceDesc -like "*Cherry*") -or (($props.DeviceDesc -like "*;Virtual Display") -and ($props.Mfg -notlike "*HW*"))
            if ($isCherryOrVdd) {
                $hasDevice = $true
                break
            }
        }
    }
    if ($pipeExists) { $hasDevice = $true }
    [pscustomobject]@{
        HasDevice = $hasDevice
        ServiceStatus = if ($svc) { $svc.Status.ToString() } else { "NotInstalled" }
        PipeAvailable = $pipeExists
    }
}

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "需要管理员权限运行驱动安装或卸载脚本。"
    }
}

function Assert-PackageIntegrity {
    if (-not (Test-Path $MsiPath) -or -not (Test-Path $CertPath)) {
        if (Test-Path $ZipPath) {
            $zipHash = (Get-FileHash $ZipPath -Algorithm SHA256).Hash
            if ($zipHash -ne $ExpectedZipSha256) {
                throw "驱动压缩包 SHA256 校验失败: $zipHash"
            }
            Expand-Archive -Path $ZipPath -DestinationPath $InstallerDir -Force
        } else {
            throw "未找到 virtual-display-rs $ExpectedVersion 安装包，请从 https://github.com/MolotovCherry/virtual-display-rs/releases 下载。 "
        }
    }

    $msiHash = (Get-FileHash $MsiPath -Algorithm SHA256).Hash
    if ($msiHash -ne $ExpectedMsiSha256) {
        throw "MSI SHA256 校验失败 ($msiHash != $ExpectedMsiSha256)"
    }

    $certHash = (Get-FileHash $CertPath -Algorithm SHA256).Hash
    if ($certHash -ne $ExpectedCertSha256) {
        throw "证书 SHA256 校验失败 ($certHash != $ExpectedCertSha256)"
    }

    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertPath)
    if ($cert.Thumbprint -ne $ExpectedThumbprint) {
        throw "驱动证书指纹不匹配: $($cert.Thumbprint)"
    }

    Write-Host "[OK] SHA256 & 证书指纹校验通过 ($ExpectedThumbprint)"
}

switch ($Action) {
    "Status" {
        $st = Test-ExistingDriver
        $owned = if (Test-Path $OwnershipPath) { Get-Content $OwnershipPath -Raw } else { "none" }
        Write-Host "DeviceInstalled=$($st.HasDevice) Service=$($st.ServiceStatus) PipeAvailable=$($st.PipeAvailable)"
        Write-Host "OwnershipMarker=$owned"
    }

    "Install" {
        Assert-Admin
        $before = Test-ExistingDriver
        if (-not (Test-Path $OwnershipPath)) {
            New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
            $marker = @{
                driver = "MolotovCherry/virtual-display-rs"
                version = $ExpectedVersion
                managedBySnugWin = (-not $before.HasDevice)
                preExistingUserDriver = $before.HasDevice
                thumbprint = $ExpectedThumbprint
                recordedAt = (Get-Date).ToString("o")
            } | ConvertTo-Json
            Set-Content -Path $OwnershipPath -Value $marker -Encoding UTF8
        }

        function Wait-DriverPipe {
            param([int]$Seconds = 8)
            $deadline = (Get-Date).AddSeconds($Seconds)
            while ((Get-Date) -lt $deadline) {
                if ((Test-ExistingDriver).PipeAvailable) {
                    return $true
                }
                Start-Sleep -Milliseconds 300
            }
            return $false
        }

        function Get-VddRootNodes {
            return @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
                Where-Object { $_.HardwareID -contains "Root\VirtualDisplayDriver" })
        }

        function Invoke-ForceBindVddInf {
            param([string]$InfPath)
            if (-not (Test-Path $InfPath)) { return $false }
            $nodes = Get-VddRootNodes
            if (-not $nodes -or $nodes.Count -eq 0) { return $false }

            if (-not ("SnugVddNewDev" -as [type])) {
                Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class SnugVddNewDev {
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent,
        string HardwareId,
        string FullInfPath,
        uint InstallFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool bRebootRequired);
}
"@
            }

            Write-Host "尝试通过 newdev.dll (INSTALLFLAG_FORCE) 强制将 Root\VirtualDisplayDriver 绑定到 $InfPath..."
            [bool]$rebootRequired = $false
            $ok = [SnugVddNewDev]::UpdateDriverForPlugAndPlayDevicesW(
                [IntPtr]::Zero,
                "Root\VirtualDisplayDriver",
                $InfPath,
                [uint32]1,
                [ref]$rebootRequired)
            if (-not $ok) {
                $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                Write-Warning "UpdateDriverForPlugAndPlayDevicesW 返回 false (Win32Error=$err)"
                return $false
            }
            if ($rebootRequired) {
                Write-Host "驱动强制绑定完成（系统提示可能需要重启生效）。"
            }
            return $true
        }

        if ($before.PipeAvailable) {
            Write-Host "[OK] 检测到已就绪的 virtual-display-rs 驱动与命名管道，保留现有安装。"
            exit 0
        }

        Assert-PackageIntegrity

        Write-Host "[1/3] 安装驱动签名证书至 Root & TrustedPublisher..."
        certutil -addstore -f root $CertPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "导入 Root 证书失败 ($LASTEXITCODE)" }
        certutil -addstore -f TrustedPublisher $CertPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "导入 TrustedPublisher 证书失败 ($LASTEXITCODE)" }

        $driverInf = Join-Path $env:ProgramFiles "VirtualDisplayDriver\driver\VirtualDisplayDriver.inf"
        if (Invoke-ForceBindVddInf -InfPath $driverInf) {
            if (Wait-DriverPipe -Seconds 8) {
                Write-Host "[OK] virtual-display-rs $ExpectedVersion 强制绑定并上线成功。"
                exit 0
            }
        }

        Write-Host "[2/3] 安装 virtual-display-driver-$ExpectedVersion-x86_64.msi..."
        $proc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$MsiPath`" /passive /norestart" -Wait -PassThru
        if ($proc.ExitCode -notin 0, 3010) { throw "msiexec 失败，退出码 $($proc.ExitCode)" }

        Write-Host "[3/3] 校验驱动服务与命名管道..."
        if (Wait-DriverPipe -Seconds 15) {
            Write-Host "[OK] virtual-display-rs $ExpectedVersion 安装并上线成功。"
            exit 0
        }

        if (Invoke-ForceBindVddInf -InfPath $driverInf) {
            if (Wait-DriverPipe -Seconds 8) {
                Write-Host "[OK] virtual-display-rs $ExpectedVersion 强制绑定并上线成功。"
                exit 0
            }
        }

        Write-Host "命名管道仍未就绪，尝试卸载重装 MSI 以重建根设备节点与驱动绑定..."
        $uninstallProc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/x `"$MsiPath`" /qn /norestart" -Wait -PassThru
        if ($uninstallProc.ExitCode -notin 0, 1605, 3010) {
            Write-Warning "msiexec /x 返回退出码 $($uninstallProc.ExitCode)，继续尝试重装..."
        }
        $reinstallProc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$MsiPath`" /qn /norestart" -Wait -PassThru
        if ($reinstallProc.ExitCode -notin 0, 3010) { throw "msiexec 修复重装失败，退出码 $($reinstallProc.ExitCode)" }

        if (Wait-DriverPipe -Seconds 15) {
            Write-Host "[OK] virtual-display-rs $ExpectedVersion 修复重装并上线成功。"
            exit 0
        }

        if (Invoke-ForceBindVddInf -InfPath $driverInf) {
            if (Wait-DriverPipe -Seconds 8) {
                Write-Host "[OK] virtual-display-rs $ExpectedVersion 强制绑定并上线成功。"
                exit 0
            }
        }

        $pnpDevices = Get-VddRootNodes
        if ($pnpDevices.Count -gt 0) {
            foreach ($dev in $pnpDevices) {
                Write-Host "尝试重启虚拟显示设备 $($dev.InstanceId) 以激活命名管道..."
                & pnputil /restart-device "$($dev.InstanceId)"
            }
            if (Wait-DriverPipe -Seconds 5) {
                Write-Host "[OK] virtual-display-rs $ExpectedVersion 安装并上线成功。"
                exit 0
            }
        }
        Write-Warning "驱动已安装，但命名管道暂未就绪（可能需要重启 Windows 或在设备管理器重启 Cherry Virtual Display）。"
    }

    "Uninstall" {
        Assert-Admin
        if (-not (Test-Path $OwnershipPath)) {
            Write-Host "未找到 SnugWin 驱动归属记录，为避免误删用户自有驱动，跳过卸载。"
            exit 0
        }
        $marker = Get-Content $OwnershipPath -Raw | ConvertFrom-Json
        if (-not $marker.managedBySnugWin) {
            Write-Host "驱动在安装 SnugWin 前已由用户自行安装，保留驱动不被卸载。"
            exit 0
        }
        if (-not (Test-Path $MsiPath)) {
            throw "需要原 MSI 文件卸载: $MsiPath"
        }
        $proc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/x `"$MsiPath`" /passive /norestart" -Wait -PassThru
        if ($proc.ExitCode -eq 0) {
            Remove-Item $OwnershipPath -Force -ErrorAction SilentlyContinue
            Write-Host "[OK] 已卸载 SnugWin 管理的 virtual-display-rs 驱动。"
        } else {
            throw "卸载 MSI 失败 ($($proc.ExitCode))"
        }
    }
}
