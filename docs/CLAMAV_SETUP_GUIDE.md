# ClamAV Setup Guide

## 🔍 How ClamAV Works

### **Architecture Overview**

```
Your Application (ASP.NET Core)
    ↓
    ↓ TCP/IP Connection (Port 3310)
    ↓
ClamAV Daemon (clamd)
    ↓
    ↓ Scans File
    ↓
Returns Result (Clean/Infected/Error)
```

### **Yes, the file IS sent to the ClamAV server!**

ClamAV works as a **client-server architecture**:

1. **ClamAV Daemon (clamd)** runs as a separate service
2. Your application connects to it via **TCP/IP** (default port 3310)
3. The **file stream is sent over the network** to the daemon
4. The daemon scans the file using its virus definitions
5. The daemon returns the scan result (Clean/Infected/Error)

### **Current Configuration**

From `appsettings.json`:
```json
"ClamAV": {
  "Enabled": true,
  "Host": "localhost",      // ← ClamAV daemon address
  "Port": 3310,              // ← Default ClamAV port
  "TimeoutSeconds": 30
}
```

**Current Setup**: `localhost` means ClamAV daemon runs on the **same machine** as your application.

---

## 🚀 Setup Options

### **Option 1: Local ClamAV (Same Machine)** ✅ Recommended for Development

**Pros:**
- Fast (no network latency)
- Simple setup
- Good for single-server deployments

**Cons:**
- Uses local machine resources
- Not scalable for multiple app servers

**Setup Steps:**

#### **Windows:**
```powershell
# Install ClamAV using Chocolatey
choco install clamav

# Or download from: https://www.clamav.net/downloads

# Start ClamAV daemon
clamd
```

#### **Linux/Docker:**
```bash
# Install ClamAV
sudo apt-get update
sudo apt-get install clamav clamav-daemon

# Start daemon
sudo systemctl start clamav-daemon
sudo systemctl enable clamav-daemon

# Update virus definitions
sudo freshclam
```

#### **Docker (Recommended):**
```yaml
# Add to docker-compose.yml
services:
  clamav:
    image: clamav/clamav:latest
    container_name: beeapp-clamav
    ports:
      - "3310:3310"
    volumes:
      - clamav-data:/var/lib/clamav
    restart: unless-stopped

volumes:
  clamav-data:
```

**Configuration:**
```json
"ClamAV": {
  "Enabled": true,
  "Host": "localhost",  // or "clamav" if in Docker network
  "Port": 3310,
  "TimeoutSeconds": 30
}
```

---

### **Option 2: Remote ClamAV Server** 🌐 Recommended for Production

**Pros:**
- Centralized scanning
- Scalable (multiple app servers can use one ClamAV)
- Better resource management

**Cons:**
- Network latency
- Requires network access
- More complex setup

**Setup Steps:**

1. **Set up ClamAV on a dedicated server:**
```bash
# On the ClamAV server
sudo apt-get install clamav clamav-daemon

# Configure /etc/clamav/clamd.conf
# Set: TCPSocket 3310
# Set: TCPAddr 0.0.0.0 (or specific IP)

sudo systemctl restart clamav-daemon
```

2. **Update your appsettings.json:**
```json
"ClamAV": {
  "Enabled": true,
  "Host": "clamav.yourdomain.com",  // ← Remote server IP/hostname
  "Port": 3310,
  "TimeoutSeconds": 30
}
```

3. **Ensure firewall allows connection:**
```bash
# On ClamAV server, allow port 3310
sudo ufw allow 3310/tcp
```

---

### **Option 3: Disable ClamAV (Development Only)** ⚠️

**For local development without ClamAV:**

```json
"ClamAV": {
  "Enabled": false,  // ← Disables scanning
  "Host": "localhost",
  "Port": 3310,
  "TimeoutSeconds": 30
}
```

**⚠️ WARNING**: Also set `RequireVirusScan: false` in FileUpload section:
```json
"FileUpload": {
  "RequireVirusScan": false  // ← Allows uploads without scan
}
```

---

## 📊 How Your Code Works

### **Scan Flow:**

```csharp
// 1. User uploads file
var fileStream = form.ItemImage.OpenReadStream();

// 2. Connect to ClamAV daemon
var client = new ClamClient("localhost", 3310);

// 3. Send file stream to ClamAV daemon over TCP/IP
var scanResult = await client.SendAndScanFileAsync(fileStream, ct);
// ↑ This sends the ENTIRE file to the ClamAV server!

// 4. Get result
if (scanResult.Result == ClamScanResults.Clean) {
    // File is safe
} else if (scanResult.Result == ClamScanResults.VirusDetected) {
    // File contains malware - REJECT
}
```

### **Network Communication:**

```
[Your App]                    [ClamAV Daemon]
    |                                |
    |--- TCP Connect (port 3310) --->|
    |                                |
    |--- Send File Stream ---------->|
    |                                |
    |                                | [Scans file]
    |                                |
    |<-- Return Result --------------|
    |                                |
```

---

## 🔧 Troubleshooting

### **Problem: "ClamAV server is not available"**

**Solutions:**
1. Check if ClamAV daemon is running:
   ```bash
   # Linux
   sudo systemctl status clamav-daemon
   
   # Windows
   # Check if clamd.exe is running
   ```

2. Test connection:
   ```bash
   # Linux
   telnet localhost 3310
   
   # Or use clamdscan
   echo "PING" | nc localhost 3310
   ```

3. Check firewall:
   ```bash
   sudo ufw status
   ```

4. Verify configuration:
   ```json
   "ClamAV": {
     "Enabled": true,
     "Host": "localhost",  // ← Correct?
     "Port": 3310          // ← Correct?
   }
   ```

### **Problem: "Timeout" errors**

**Solutions:**
1. Increase timeout:
   ```json
   "ClamAV": {
     "TimeoutSeconds": 60  // ← Increase from 30
   }
   ```

2. Check ClamAV daemon logs:
   ```bash
   sudo journalctl -u clamav-daemon -f
   ```

3. Check network latency (if remote):
   ```bash
   ping clamav.yourdomain.com
   ```

### **Problem: "Connection refused"**

**Solutions:**
1. ClamAV daemon not running - start it
2. Wrong port - verify port 3310
3. Firewall blocking - check firewall rules
4. Wrong host - verify hostname/IP

---

## 🐳 Docker Setup (Recommended)

### **docker-compose.yml:**

```yaml
version: '3.8'

services:
  # Your existing services...
  
  clamav:
    image: clamav/clamav:latest
    container_name: beeapp-clamav
    ports:
      - "3310:3310"
    volumes:
      - clamav-data:/var/lib/clamav
      - clamav-logs:/var/log/clamav
    environment:
      - CLAMD_CONF_TCPSocket=3310
      - CLAMD_CONF_TCPAddr=0.0.0.0
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "clamdscan", "--version"]
      interval: 30s
      timeout: 10s
      retries: 3

volumes:
  clamav-data:
  clamav-logs:
```

### **Update appsettings.json for Docker:**

```json
"ClamAV": {
  "Enabled": true,
  "Host": "clamav",  // ← Docker service name
  "Port": 3310,
  "TimeoutSeconds": 30
}
```

### **Or use environment variables:**

```yaml
# In docker-compose.yml for backend service
environment:
  ClamAV__Enabled: "true"
  ClamAV__Host: "clamav"
  ClamAV__Port: "3310"
  ClamAV__TimeoutSeconds: "30"
```

---

## 📝 Security Notes

### **Network Security:**

1. **Local (localhost)**: ✅ Secure - no network exposure
2. **Remote**: ⚠️ Use VPN or private network
3. **Public Internet**: ❌ **NOT RECOMMENDED** - files sent unencrypted!

### **Performance:**

- **Local**: ~10-50ms per file
- **Remote (same datacenter)**: ~50-200ms per file
- **Remote (internet)**: ~200-1000ms+ per file

### **File Size:**

- ClamAV can handle large files
- Your 10MB limit is fine
- Network transfer time increases with file size

---

## ✅ Quick Start Checklist

- [ ] Install ClamAV daemon (or use Docker)
- [ ] Start ClamAV daemon
- [ ] Update virus definitions: `freshclam`
- [ ] Verify connection: `telnet localhost 3310`
- [ ] Update `appsettings.json` with correct Host/Port
- [ ] Set `Enabled: true`
- [ ] Test file upload
- [ ] Check logs for scan results

---

## 🎯 Summary

**Yes, ClamAV sends files over the network!**

- **Local setup**: File sent to localhost (same machine) - fast and secure
- **Remote setup**: File sent to remote server - requires network access
- **Docker setup**: File sent to ClamAV container - recommended for production

The file stream is sent via TCP/IP to the ClamAV daemon, which scans it and returns the result. This is the standard ClamAV architecture.
