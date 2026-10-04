"""
Self-hosted face match service for per-shift driver checks.

Computes InsightFace (buffalo_l) embeddings and cosine similarity so the backend
can compare a shift selfie against the driver's KYC reference selfie.

Endpoints:
  GET  /v1/health  -> {"status": "ok", "model": "buffalo_l"}
  POST /v1/embed   -> multipart 'file' (image) -> {"embedding": [512 floats], "faces": N, "latency_ms": x}
  POST /v1/verify  -> multipart 'file' + form 'reference_embedding' (JSON array)
                      -> {"match_score": cosine -1..1, "faces": N, "latency_ms": x}

Embeddings are L2-normalized, so cosine similarity is the dot product.
"""

import json
import os
import time

import cv2
import numpy as np
from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from insightface.app import FaceAnalysis

MODEL_NAME = os.environ.get("MODEL_NAME", "buffalo_l")
DET_SIZE = int(os.environ.get("DET_SIZE", "640"))

app = FastAPI(title="Bee Face Match", version="1.0")

analyzer = FaceAnalysis(name=MODEL_NAME, providers=["CPUExecutionProvider"])
analyzer.prepare(ctx_id=-1, det_size=(DET_SIZE, DET_SIZE))


def _decode_image(data: bytes) -> np.ndarray:
    img = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
    if img is None:
        raise HTTPException(status_code=400, detail="Could not decode image")
    return img


def _largest_face_embedding(img: np.ndarray):
    faces = analyzer.get(img)
    if not faces:
        return None, 0
    largest = max(faces, key=lambda f: (f.bbox[2] - f.bbox[0]) * (f.bbox[3] - f.bbox[1]))
    embedding = largest.normed_embedding
    return embedding, len(faces)


@app.get("/v1/health")
def health():
    return {"status": "ok", "model": MODEL_NAME}


@app.post("/v1/embed")
async def embed(file: UploadFile = File(...)):
    started = time.monotonic()
    img = _decode_image(await file.read())
    embedding, face_count = _largest_face_embedding(img)
    latency_ms = round((time.monotonic() - started) * 1000, 1)
    if embedding is None:
        return {"embedding": None, "faces": 0, "latency_ms": latency_ms}
    return {"embedding": embedding.tolist(), "faces": face_count, "latency_ms": latency_ms}


@app.post("/v1/verify")
async def verify(file: UploadFile = File(...), reference_embedding: str = Form(...)):
    started = time.monotonic()
    try:
        reference = np.asarray(json.loads(reference_embedding), dtype=np.float32)
    except (ValueError, TypeError):
        raise HTTPException(status_code=400, detail="reference_embedding must be a JSON float array")
    if reference.ndim != 1 or reference.size == 0:
        raise HTTPException(status_code=400, detail="reference_embedding must be a non-empty 1-D array")

    norm = np.linalg.norm(reference)
    if norm == 0:
        raise HTTPException(status_code=400, detail="reference_embedding must be non-zero")
    reference = reference / norm

    img = _decode_image(await file.read())
    embedding, face_count = _largest_face_embedding(img)
    latency_ms = round((time.monotonic() - started) * 1000, 1)
    if embedding is None:
        return {"match_score": None, "faces": 0, "latency_ms": latency_ms}
    if embedding.shape != reference.shape:
        raise HTTPException(status_code=400, detail=f"reference_embedding size {reference.size} != model size {embedding.size}")

    score = float(np.dot(embedding, reference))
    return {"match_score": score, "faces": face_count, "latency_ms": latency_ms}
