# Winhance Architecture & AxePower Integration Blueprint 🪟

A technical analysis of **Memory's Tech Tips Winhance** ([github.com/memstechtips/Winhance](https://github.com/memstechtips/Winhance) / [winhance.net](https://winhance.net)) and how its core mechanisms, architecture, and features enhance the **AxePower** .NET 8 WPF application.

---

## 1. Executive Summary & Winhance Overview

**Winhance** is a modern C#/WinUI 3 utility built by Marco du Plessis (*memstechtips*) designed for deep Windows 10/11 debloating, performance optimization, UI customization, and unattended deployment generation (integrating the legacy *WIMUtil* project).

### Core Pillars of Winhance
1. **System Transparency (Zero Black-Boxes)**: Direct visibility into the exact registry keys, values, and PowerShell commands behind every tweak.
2. **Persistent Update Protection**: Methods to ensure removed bloatware and custom OEM drivers are not overridden by monthly Windows Cumulative Updates.
3. **Builder & Unattended Mode**: Ability to compose optimization profiles and export them as `autounattend.xml` answer files without applying changes to the live system.
4. **Gaming & Low Latency Security Toggles**: Granular toggles for VBS (Virtualization-Based Security) and HVCI (Hypervisor-Enforced Code Integrity).
5. **Quality-of-Life & Visual Effects**: Fast Start Menu / File Explorer cleanup and DWM visual effect presets.

---

## 2. Feature Matrix: Winhance vs AxePower

| Winhance Feature | Underlying Mechanism | AxePower Synergy | Recommended AxePower Implementation |
| :--- | :--- | :--- | :--- |
| **Driver Overwrite Protection** | `ExcludeWUDriversInQualityUpdate = 1` | ⭐⭐⭐⭐⭐ Critical | **System Optimizer > Update Shield** |
| **App Re-Install Immunity** | Appx de-provisioning + scheduled task hooks | ⭐⭐⭐⭐⭐ Critical | **Uninstaller Pro > Persistent Debloat** |
| **Technical Inspector Panel** | Live Registry Diff & Command Drawer | ⭐⭐⭐⭐⭐ High UX | **Interactive Inspector Flyout** |
| **VBS / HVCI Toggle** | `HypervisorEnforcedCodeIntegrity\Enabled` | ⭐⭐⭐⭐⭐ Gaming | **Beast / Game Mode Toggle** |
| **Clean Start & AI Removal** | `HideRecommendedSection`, `BingSearchEnabled=0` | ⭐⭐⭐⭐ High | **System Optimizer > Win11 Studio** |
| **File Explorer Studio** | `TakeOwnership` CLSID, `HideFileExt`, `CompactMode` | ⭐⭐⭐⭐ High | **System Optimizer > Explorer Tools** |
| **Change Audit & Rollback** | Timestamped state history JSON | ⭐⭐⭐⭐⭐ Safety | **Audit Trail & 1-Click Rollback** |
| **Builder / Unattended Mode** | `autounattend.xml` XML serialization | ⭐⭐⭐⭐ Power-User | **Fresh-Start Profile Generator** |
| **DWM Visual Effects** | `VisualFXSetting`, Window animation flags | ⭐⭐⭐⭐ Battery | **Eco Mode Power Scaling** |

---

## 3. Deep Technical Blueprints for AxePower

### 1. 🛡️ Windows Update Driver Lock & Bloatware Re-Install Immunity
* **Driver Protection:**
  ```registry
  [HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate]
  "ExcludeWUDriversInQualityUpdate"=dword:00000001
  ```
  *Benefit:* Prevents Windows Update from replacing OEM-tuned GPU (NVIDIA/AMD/Intel) and audio drivers with generic Microsoft DCH packages.
* **Bloatware Immunity:**
  ```powershell
  # Deprovision all users package to prevent reinstallation on new user accounts / feature updates
  Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like "*$PackageName*" } | Remove-AppxProvisionedPackage -Online
  ```
  ```registry
  [HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent]
  "DisableWindowsConsumerFeatures"=dword:00000001
  ```

---

### 2. 🔍 Interactive Technical Inspector (Transparency Engine)
* **Goal:** When the user clicks or hovers over any toggle in `SystemOptimizerView` or `GameModeView`, an expandable side drawer reveals:
  * **Registry Key:** e.g., `HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection`
  * **Value Name / Type:** `AllowTelemetry` (`REG_DWORD`)
  * **Target State:** `0` (Disabled) vs Default `1` (Full)
  * **Underlying Command:** C# Win32 Registry API / PowerShell script
  * **Impact Level:** `Safe` / `Caution` / `Advanced`
  * **Requires Reboot:** `No` / `Restart Explorer` / `System Reboot`

---

### 3. ⚡ Core Isolation & VBS (Virtualization-Based Security) Switcher
* **Mechanism:**
  ```registry
  [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity]
  "Enabled"=dword:00000000
  ```
* **AxePower Integration:**
  * In **Beast / Game Mode**: Offer an optional toggle for competitive esports players (yielding 5–15% CPU FPS gain in CPU-bound games like CS2, Valorant, Warzone).
  * In **Smart / Eco Mode**: Restore `Enabled = 1` for hypervisor-level kernel security.
  * *Safety Requirement:* Always prompt user with a clean confirmation dialog and explain the security vs performance tradeoff.

---

### 4. 🧼 Clean Start Menu & Windows AI Neutralizer
* **Hide "Recommended" Section in Windows 11 Start Menu:**
  ```registry
  [HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Explorer]
  "HideRecommendedSection"=dword:00000001
  ```
* **Disable Bing Web Search (Instant Start Menu Search):**
  ```registry
  [HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\Explorer]
  "DisableSearchBoxSuggestions"=dword:00000001

  [HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search]
  "BingSearchEnabled"=dword:00000000
  ```
* **Disable Windows Recall / AI Telemetry Analysis:**
  ```registry
  [HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI]
  "DisableAIDataAnalysis"=dword:00000001
  ```

---

### 5. 📁 Power-User File Explorer Studio
* **"Take Ownership" Shell Extension:**
  ```registry
  [HKEY_CLASSES_ROOT\*\shell\runas]
  @="Take Ownership"
  "NoWorkingDirectory"=""

  [HKEY_CLASSES_ROOT\*\shell\runas\command]
  @="cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant administrators:F"
  "IsolatedCommand"="cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant administrators:F"
  ```
* **Show File Extensions & Hidden Files:**
  ```registry
  [HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced]
  "HideFileExt"=dword:00000000
  "Hidden"=dword:00000001
  "UseCompactMode"=dword:00000001
  ```
* **1-Click Restart Explorer:**
  * Kill and restart `explorer.exe` safely via C# `Process.Start("explorer.exe")`.

---

### 6. 📜 Visual Change Audit Timeline & 1-Click Rollback
* **Architecture:**
  * Whenever a tweak is applied, AxePower serializes an `AuditEntry`:
    ```csharp
    public record AuditEntry(
        DateTime Timestamp,
        string TweakId,
        string TweakName,
        string RegistryPath,
        string ValueName,
        object OldValue,
        object NewValue,
        bool IsReverted
    );
    ```
  * Saved to `%LocalAppData%\AxePower\tweak_audit.json`.
  * Displayed in `LogsView.xaml` with an interactive "Revert" button for each individual entry.

---

### 7. 📦 Fresh-Start Builder & Unattended Deployment (`autounattend.xml`)
* **Feature:**
  * Power users setting up a new gaming laptop or clean Windows installation can generate a customized `autounattend.xml` directly from AxePower.
  * Injects:
    * OOBE Bypass (`BypassTPMCheck`, `BypassRAMCheck`, `BypassSecureBootCheck`, `BypassNRO` for offline local accounts).
    * Pre-configured AxePower power schemes (Beast, Eco, Smart).
    * Auto-installation of AxePower and chosen WinGet runtimes (DirectX, VC++ All-in-One).
