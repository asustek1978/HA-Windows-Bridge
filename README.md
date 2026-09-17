# HA Windows Bridge v1.0

A Windows 11 x64 tray client for Home Assistant. It registers the PC through the built-in mobile_app integration, sends diagnostic sensors, and receives Home Assistant notifications over an outbound WebSocket. There are no incoming Windows ports or custom Home Assistant integrations.

## Install

Download the HAWindowsBridge_v1.0_win-x64 artifact from the latest successful [Windows build](https://github.com/asustek1978/HA-Windows-Bridge/actions/workflows/windows-build.yml), extract HAWindowsBridge.exe to a permanent folder, and launch it. The self-contained build includes the .NET runtime; Windows may show an unknown publisher warning because this build is not signed.

Open the tray icon's Settings. Enter:

- Local URL: for example, http://192.168.1.100:8123 (optional; only used on your home network).
- External URL: for example, https://ha.polyarca.ru (optional if the local URL is set). An external URL must use HTTPS.
- A Home Assistant long-lived access token from your profile under Security.
- Update interval (30 seconds by default) and optional Windows login autostart.

At least one URL and the token are required. Click Save; the status in the tray shows LAN or Remote. The same Home Assistant instance must be reachable through both URLs. On a different network the external URL is used. HTTPS certificates are verified by Windows. For a local HTTP URL, the access token travels unencrypted on your LAN; prefer HTTPS or a trusted VPN if available.

The token and mobile_app webhook ID are encrypted with Windows DPAPI for the current user and stored under %LOCALAPPDATA%\HAWindowsBridge. Keep the EXE at its installed path if autostart is enabled. Do not share the settings file or token.

## Sensors

The device appears under Settings → Devices & services → Mobile App. The actual entity IDs depend on your PC name and any existing HA entities. Check the device page instead of assuming fixed IDs.

| Sensor | Unit | Notes |
| --- | --- | --- |
| CPU usage | % | Whole system |
| RAM usage | % | Physical memory |
| Idle time | min | Time since keyboard/mouse input |
| Uptime | h | Time since Windows boot |
| IP address | text | First active network adapter with a gateway |
| Download speed / Upload speed | Mbit/s | Selected adapter |
| Last seen | timestamp | Updated each reporting cycle |
| Battery level | % | Only for PCs with a battery |
| Session locked | on/off | Appears after the first lock or unlock event while the client is running |
| Display on | on/off | Appears after the first Windows display power event |

When the client or PC goes offline, HA retains the last reading. Use the Last seen timestamp to detect a stale device; an always-on online boolean would incorrectly remain on.

## Notifications

The registration enables a notify action for this PC. Find the exact notify.mobile_app_... action name in HA Developer tools → Actions and send:

    action: notify.mobile_app_your_pc
    data:
      title: Home Assistant
      message: Test from HA

The tray client must be running and connected. This first version shows Windows tray notifications with a title and message. Windows Focus/Do Not Disturb settings may suppress the banner. Images, replacement tags, action buttons, and PC control are planned for subsequent versions; they are not implemented here. If the notify action does not appear immediately, restart Home Assistant once.

## Build locally

Install .NET 10 SDK on Windows 11 x64 and run:

    dotnet publish src/HaWindowsBridge/HaWindowsBridge.csproj -c Release -r win-x64 --self-contained true -o dist

The executable is dist/HAWindowsBridge.exe. The GitHub Actions workflow uses the same command and attaches the EXE as a build artifact. No installer, code signing, or release has been published yet.

## Protocol

Registration: authenticated POST /api/mobile_app/registrations. Sensors: register_sensor once per entity, then update_sensor_states in batches via the mobile_app webhook. Notifications: authenticated /api/websocket with mobile_app/push_notification_channel. Encryption of the webhook payload is not requested: HTTPS/WSS is required for the external address; the local HTTP option is explicitly under your control. No received notification content is executed as a command.

Based on the published Home Assistant native app API documentation; source code is original and does not include code from HASSConnect.
