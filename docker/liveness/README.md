# YOLO Liveness Detection Service

This Docker container runs the YOLO-based face liveness detection service for anti-spoofing.

## Model File Requirements

The service requires a trained YOLO model file (`anti_spoofing.pt`) to function. This file is typically several megabytes in size.

## Obtaining the Model File

### Option 1: Download from Repository (if available)

The Dockerfile will attempt to download the model file automatically. If this fails, you can manually download it:

1. Visit the repository: https://github.com/AdnanSattar/yolo-liveness-detector
2. Check the `model/` directory for `anti_spoofing.pt`
3. If the file is stored using Git LFS, you'll need to:
   - Clone the repository with Git LFS installed
   - Run `git lfs pull` to download LFS files
   - Copy `model/anti_spoofing.pt` to your host machine

### Option 2: Train Your Own Model

Follow the training documentation in the repository:
- See `training/` directory for data collection and training scripts
- Train a custom model for your specific environment
- Export the trained model as `anti_spoofing.pt`

### Option 3: Use a Pre-trained Model from Releases

Check the repository's Releases page for pre-trained model files.

## Running the Container

### With Volume Mount (Recommended)

Mount the model file as a volume:

```bash
docker build -t yolo-liveness -f docker/liveness/Dockerfile .
docker run -d \
  -p 8000:8000 \
  -v /path/to/anti_spoofing.pt:/app/model/anti_spoofing.pt \
  --name yolo-liveness \
  yolo-liveness
```

### Without Volume Mount

If the model file is successfully downloaded during build, you can run without a volume:

```bash
docker build -t yolo-liveness -f docker/liveness/Dockerfile .
docker run -d -p 8000:8000 --name yolo-liveness yolo-liveness
```

## Troubleshooting

### Error: "invalid load key, 'v'"

This error indicates the model file is corrupted, empty, or contains text instead of binary data. This typically happens when:

1. **Git LFS pointer file**: The file in the repository is a Git LFS pointer, not the actual model
   - **Solution**: Download the actual model file using Git LFS or mount a valid model file

2. **Empty or corrupted file**: The file didn't download correctly
   - **Solution**: Re-download or obtain a valid model file

3. **Wrong file format**: The file is not a valid PyTorch model
   - **Solution**: Ensure you're using a valid `.pt` file trained with YOLOv8

### Verifying the Model File

Check if your model file is valid:

```bash
# Check file size (should be several MB, not just a few KB)
ls -lh /path/to/anti_spoofing.pt

# Check file type (should show binary data)
file /path/to/anti_spoofing.pt

# If it shows "ASCII text" or is very small (< 1KB), it's likely a Git LFS pointer
```

### Health Check

Once running, verify the service is healthy:

```bash
curl http://localhost:8000/v1/health
```

Expected response:
```json
{
  "status": "healthy",
  "model_loaded": true,
  "device": "cpu",
  "uptime_seconds": 123
}
```

## Environment Variables

- `MODEL_PATH`: Path to the model file (default: `/app/model/anti_spoofing.pt`)
- `DEVICE`: Device to use for inference (`auto`, `cpu`, or `cuda`) (default: `auto`)
- `CONFIDENCE_THRESHOLD`: Confidence threshold for detections (default: `0.5`)

## API Endpoints

- `GET /v1/health`: Health check endpoint
- `POST /v1/predict`: Submit an image for liveness detection

See the main repository documentation for API details.
