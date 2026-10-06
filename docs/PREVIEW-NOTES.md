Windows x64 · Windows 10/11 · Preview for network discovery testing

- In Settings → Network, leave discovery on all interfaces or select one local Wi-Fi/Ethernet interface. Available adapters appear above unavailable ones. Only AirPlay discovery is restricted; manually added receivers and audio routing are unchanged.
- On a PC connected to ZeroTier or another virtual network, compare the receiver list in both modes. The selected LAN should show local HomePods and exclude co-workers' remote receivers. Check that playback still works.
- Disconnect and reconnect the selected interface; discovery should pause while it is unavailable and recover afterwards. Verify the choice survives restart.
- Please share the adapter type/name, receiver lists in both modes, and diagnostics/logs when reporting results. Do not include private addresses or device identifiers in public reports.
- Download AirFlash-X.Y.Z.msi to install or AirFlash.exe for portable use; verify SHA256SUMS.txt. Binaries are unsigned. Preview results will determine whether this change is merged into main.
