# YOLO Liveness Model Files

This directory contains the YOLO model file (`anti_spoofing.pt`) required for the liveness detection service.

## Model File

- **File**: `anti_spoofing.pt`
- **Size**: ~83.57 MB
- **Source**: https://github.com/AdnanSattar/yolo-liveness-detector

## Obtaining the Model File

The model file is stored using Git LFS (Large File Storage). To obtain it:

### Option 1: Clone with Git LFS (Recommended)

```bash
git clone https://github.com/AdnanSattar/yolo-liveness-detector.git
cd yolo-liveness-detector
git lfs install
git lfs pull
# Copy the model file
cp model/anti_spoofing.pt /path/to/bee-app-backend-v2/models/
```

### Option 2: Download Script

If you have Git LFS installed, you can use:

```bash
cd C:\Dev\BEEAPP
git clone https://github.com/AdnanSattar/yolo-liveness-detector.git
cd yolo-liveness-detector
git lfs pull
# The model will be in model/anti_spoofing.pt
```

### Option 3: Manual Download

1. Visit: https://github.com/AdnanSattar/yolo-liveness-detector
2. Check the Releases page for pre-built model files
3. Or follow the training documentation to train your own model

## Verification

To verify the model file is valid (not a Git LFS pointer):

```powershell
# Check file size (should be ~83 MB, not just a few KB)
Get-Item models\anti_spoofing.pt | Select-Object Name, @{Name="Size(MB)";Expression={[math]::Round($_.Length/1MB, 2)}}

# Check first line (should NOT start with "version")
Get-Content models\anti_spoofing.pt -TotalCount 1
```

If the first line starts with "version", it's a Git LFS pointer and you need to pull the actual file using `git lfs pull`.

## Docker Usage

The model file is automatically mounted into the liveness container via docker-compose.yml:

```yaml
volumes:
  - ./models/anti_spoofing.pt:/app/model/anti_spoofing.pt:ro
```

This ensures the container always uses the correct model file from your local filesystem.
