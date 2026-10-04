# Face liveness detection (YOLO anti-spoofing) - self-hosted service
# Based on https://github.com/AdnanSattar/yolo-liveness-detector
# Port 8000 is used (does not conflict with backend API: 8080/8081/8082 in CI)
FROM python:3.10-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    git-lfs \
    curl \
    libgl1 \
    libglib2.0-0 \
    build-essential \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Initialize Git LFS before cloning
RUN git lfs install

# Clone the YOLO liveness detector repo (single layer to keep image smaller)
# Use full clone to ensure LFS files are included
RUN git clone https://github.com/AdnanSattar/yolo-liveness-detector.git .

# Pull LFS files explicitly
RUN git lfs pull || true

RUN pip install --no-cache-dir -r requirements.txt

# Model directory: mount a volume at /app/model with anti_spoofing.pt for production
# See repo training docs to train or obtain a model
RUN mkdir -p /app/model

# Check if model file exists in cloned repo and validate it
# If model file is missing or appears to be a Git LFS pointer (small file), try to download it directly
RUN if [ -f "/app/model/anti_spoofing.pt" ]; then \
        file_size=$(stat -c%s /app/model/anti_spoofing.pt 2>/dev/null || stat -f%z /app/model/anti_spoofing.pt 2>/dev/null || echo "0"); \
        if [ "$file_size" -lt 1000 ]; then \
            echo "Model file found but too small ($file_size bytes). May be a Git LFS pointer. Attempting direct download..."; \
            rm -f /app/model/anti_spoofing.pt; \
            curl -L -o /app/model/anti_spoofing.pt "https://github.com/AdnanSattar/yolo-liveness-detector/raw/main/model/anti_spoofing.pt" || \
            curl -L -o /app/model/anti_spoofing.pt "https://raw.githubusercontent.com/AdnanSattar/yolo-liveness-detector/main/model/anti_spoofing.pt" || \
            echo "Direct download failed. You will need to mount a volume with the model file."; \
        else \
            echo "Model file found in repository ($file_size bytes)"; \
        fi; \
    else \
        echo "Model file not found in repository. Attempting direct download..."; \
        curl -L -o /app/model/anti_spoofing.pt "https://github.com/AdnanSattar/yolo-liveness-detector/raw/main/model/anti_spoofing.pt" || \
        curl -L -o /app/model/anti_spoofing.pt "https://raw.githubusercontent.com/AdnanSattar/yolo-liveness-detector/main/model/anti_spoofing.pt" || \
        echo "Direct download failed. You will need to mount a volume with the model file at /app/model/anti_spoofing.pt"; \
    fi

# Validate model file exists and is not empty/corrupted
# Check if file is a Git LFS pointer (starts with "version") or too small
RUN if [ -f "/app/model/anti_spoofing.pt" ]; then \
        file_size=$(stat -c%s /app/model/anti_spoofing.pt 2>/dev/null || stat -f%z /app/model/anti_spoofing.pt 2>/dev/null || echo "0"); \
        first_bytes=$(head -c 20 /app/model/anti_spoofing.pt 2>/dev/null || echo ""); \
        if [ "$file_size" -lt 1000 ] || echo "$first_bytes" | grep -q "^version"; then \
            echo "================================================"; \
            echo "ERROR: Model file is invalid or corrupted!"; \
            echo "================================================"; \
            echo "File size: $file_size bytes"; \
            echo "First bytes: $first_bytes"; \
            echo ""; \
            echo "This appears to be a Git LFS pointer file, not the actual model."; \
            echo ""; \
            echo "SOLUTIONS:"; \
            echo "1. Mount a valid model file as a volume:"; \
            echo "   docker run -v /path/to/anti_spoofing.pt:/app/model/anti_spoofing.pt ..."; \
            echo ""; \
            echo "2. Train your own model (see docker/liveness/README.md)"; \
            echo ""; \
            echo "3. Obtain the model file manually:"; \
            echo "   - Clone the repo: git clone https://github.com/AdnanSattar/yolo-liveness-detector"; \
            echo "   - Install Git LFS: git lfs install"; \
            echo "   - Pull LFS files: git lfs pull"; \
            echo "   - Copy model/anti_spoofing.pt to your host"; \
            echo ""; \
            rm -f /app/model/anti_spoofing.pt; \
            exit 1; \
        else \
            echo "✓ Model file validated successfully: $file_size bytes"; \
        fi; \
    else \
        echo "================================================"; \
        echo "WARNING: Model file not found!"; \
        echo "================================================"; \
        echo "The service will fail to start without a valid model file."; \
        echo "Please mount a volume with the model:"; \
        echo "  docker run -v /path/to/anti_spoofing.pt:/app/model/anti_spoofing.pt ..."; \
        echo "See docker/liveness/README.md for more information."; \
    fi

EXPOSE 8000

ENV MODEL_PATH=/app/model/anti_spoofing.pt
ENV DEVICE=auto
ENV CONFIDENCE_THRESHOLD=0.5

HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8000/v1/health')" || exit 1

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
