# Face match service (InsightFace buffalo_l embeddings) - self-hosted, CPU-only.
# Used for per-shift driver face checks against the KYC reference selfie.
# Port 8001 (liveness YOLO service uses 8000).
FROM python:3.10-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    libgl1 \
    libglib2.0-0 \
    build-essential \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Pre-download the buffalo_l model pack (~300MB) at build time so the container
# starts without internet access.
RUN python -c "from insightface.app import FaceAnalysis; FaceAnalysis(name='buffalo_l', providers=['CPUExecutionProvider']).prepare(ctx_id=-1)"

COPY app.py .

EXPOSE 8001

ENV MODEL_NAME=buffalo_l
ENV DET_SIZE=640

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8001/v1/health')" || exit 1

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8001"]
