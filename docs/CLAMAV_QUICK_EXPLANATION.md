# ClamAV Quick Explanation

## 🎯 Simple Answer

**YES, ClamAV sends the file to a server!**

Your application connects to a **ClamAV daemon** (a separate service) and sends the file over the network for scanning.

---

## 📡 How It Works (Simple)

```
┌─────────────────┐         TCP/IP         ┌──────────────────┐
│  Your App       │  ───────────────────>  │  ClamAV Daemon   │
│  (ASP.NET Core) │  (Sends file stream)    │  (clamd)         │
│                 │  <───────────────────  │                  │
│                 │  (Returns scan result) │                  │
└─────────────────┘                        └──────────────────┘
```

### **Step-by-Step:**

1. **User uploads file** → Your app receives it
2. **Your app connects** → Opens TCP connection to ClamAV daemon (port 3310)
3. **File is sent** → Entire file stream sent over network to ClamAV
4. **ClamAV scans** → Checks file against virus definitions
5. **Result returned** → Clean/Infected/Error sent back to your app
6. **Your app decides** → Accept or reject the file

---

## 🔧 Your Current Setup

**From `appsettings.json`:**
```json
"ClamAV": {
  "Enabled": true,
  "Host": "localhost",    // ← ClamAV daemon address
  "Port": 3310,            // ← Network port
  "TimeoutSeconds": 30
}
```

**What this means:**
- `Host: "localhost"` = ClamAV runs on the **same machine** as your app
- `Port: 3310` = Standard ClamAV network port
- File is sent to `localhost:3310` via TCP/IP

---

## 💻 Code Flow

Looking at your `ClamAVScanner.cs`:

```csharp
// Line 80: This sends the file to ClamAV daemon!
var scanResult = await client.SendAndScanFileAsync(fileStream, ct);
// ↑ The ENTIRE file stream is sent over TCP/IP to the ClamAV server
```

**What happens:**
1. `CreateClient()` → Creates TCP connection to `localhost:3310`
2. `SendAndScanFileAsync()` → **Sends file stream over network**
3. ClamAV daemon receives file, scans it
4. Returns result (Clean/Infected/Error)

---

## 🌐 Network Communication

**Current Setup (localhost):**
```
Your App (localhost) ──TCP:3310──> ClamAV Daemon (localhost)
         │                              │
         │  File Stream (10MB image)    │
         └──────────────────────────────┘
```

**Remote Setup (if you change Host):**
```
Your App (Server A) ──TCP:3310──> ClamAV Daemon (Server B)
         │                              │
         │  File Stream over network    │
         └──────────────────────────────┘
```

---

## ⚠️ Important Notes

### **1. File IS Sent Over Network**
- The entire file stream is transmitted
- For 10MB image = 10MB sent over network
- If `localhost` = fast (same machine)
- If remote = slower (network latency)

### **2. No Encryption by Default**
- ClamAV uses plain TCP (not encrypted)
- **OK for localhost** ✅
- **OK for private network** ✅
- **NOT recommended for public internet** ❌

### **3. ClamAV Daemon Must Be Running**
- ClamAV is a **separate service** (not part of your app)
- Must be installed and running
- If not running → scan fails → file rejected (fail-secure)

---

## 🚀 Quick Setup

### **Option 1: Install ClamAV Locally**

**Windows:**
```powershell
choco install clamav
clamd  # Start daemon
```

**Linux:**
```bash
sudo apt-get install clamav clamav-daemon
sudo systemctl start clamav-daemon
sudo freshclam  # Update virus definitions
```

### **Option 2: Use Docker**

Add to `docker-compose.yml`:
```yaml
services:
  clamav:
    image: clamav/clamav:latest
    ports:
      - "3310:3310"
```

Update `appsettings.json`:
```json
"ClamAV": {
  "Host": "clamav"  // ← Docker service name
}
```

### **Option 3: Disable (Development Only)**

```json
"ClamAV": {
  "Enabled": false
},
"FileUpload": {
  "RequireVirusScan": false
}
```

---

## ✅ Summary

**Question:** Does ClamAV send the file to a server?  
**Answer:** **YES!** The file is sent over TCP/IP to the ClamAV daemon.

**Your current setup:**
- ✅ Sends to `localhost:3310` (same machine)
- ✅ Fast and secure for local setup
- ⚠️ Requires ClamAV daemon to be running

**Need help setting up?** See `CLAMAV_SETUP_GUIDE.md` for detailed instructions!
