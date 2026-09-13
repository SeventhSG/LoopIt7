# Reads and restores the default playback and recording devices.
#
# Windows can make a newly arrived endpoint the default, which would silently route everything
# the user plays into the cable instead of their speakers. install.ps1 takes a snapshot before
# the driver goes in and puts back whatever changed. Dot-source this file; it defines
# Get-DefaultEndpoints and Restore-DefaultEndpoints.
#
# IPolicyConfig is the undocumented interface the Sound control panel uses (the app's
# EndpointControl.cs uses the same one). Only the one method called here is typed; the slots
# before it exist to keep the vtable lined up.

if (-not ('LoopIt7Setup.DefaultEndpoints' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace LoopIt7Setup
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    public static class DefaultEndpoints
    {
        public static string Get(int dataFlow, int role)
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(dataFlow, role, out device) != 0 || device == null)
            {
                return null;
            }
            string id;
            return device.GetId(out id) == 0 ? id : null;
        }

        public static int Set(string deviceId, int role)
        {
            var config = (IPolicyConfig)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")));
            return config.SetDefaultEndpoint(deviceId, role);
        }
    }
}
'@
}

# eRender = 0, eCapture = 1; eConsole = 0, eMultimedia = 1, eCommunications = 2.
$script:EndpointSlots = foreach ($flow in 0, 1) { foreach ($role in 0, 1, 2) { [pscustomobject]@{ Flow = $flow; Role = $role } } }

function Get-DefaultEndpoints {
    foreach ($slot in $script:EndpointSlots) {
        [pscustomobject]@{
            Flow = $slot.Flow
            Role = $slot.Role
            Id   = [LoopIt7Setup.DefaultEndpoints]::Get($slot.Flow, $slot.Role)
        }
    }
}

function Restore-DefaultEndpoints([object[]]$Snapshot) {
    $names = @{ 0 = 'playback'; 1 = 'recording' }
    $roles = @{ 0 = 'default'; 1 = 'multimedia'; 2 = 'communications' }

    foreach ($before in $Snapshot) {
        if (-not $before.Id) { continue }
        $now = [LoopIt7Setup.DefaultEndpoints]::Get($before.Flow, $before.Role)
        if ($now -eq $before.Id) { continue }

        $hr = [LoopIt7Setup.DefaultEndpoints]::Set($before.Id, $before.Role)
        $what = "$($roles[$before.Role]) $($names[$before.Flow]) device"
        if ($hr -eq 0) {
            Write-Host "  Windows moved the $what to the cable; put it back." -ForegroundColor Yellow
        } else {
            Write-Host ("  Windows moved the $what to the cable and it could not be put back (0x{0:X8})." -f $hr) -ForegroundColor Yellow
            Write-Host '  Set it back in Settings > System > Sound.' -ForegroundColor Yellow
        }
    }
}
