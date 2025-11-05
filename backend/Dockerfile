# Multi-stage Dockerfile: build frontend then runtime image for backend
FROM node:18-bullseye AS frontend-builder
WORKDIR /app/frontend
COPY frontend/package*.json ./
RUN npm ci --silent
COPY frontend/ ./
RUN npm run build

FROM python:3.12-slim
WORKDIR /app

# Install runtime deps required by OpenCV and scientific stack
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    libgl1 \
    libglib2.0-0 \
    libxrender1 \
    libxext6 \
    libsm6 \
    libgomp1 \
    libffi-dev \
    wget \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Copy backend source
COPY backend/ ./backend/

# Copy built frontend assets
COPY --from=frontend-builder /app/frontend/dist ./backend/frontend_dist

WORKDIR /app/backend

# Install Python deps
RUN python -m pip install --upgrade pip setuptools wheel
# Install CPU-only PyTorch first to avoid CUDA dependencies
RUN python -m pip install torch==2.5.1+cpu torchvision==0.20.1+cpu --index-url https://download.pytorch.org/whl/cpu --no-cache-dir
# Install other requirements (excluding torch which we already installed)
RUN if [ -f requirements-no-torch.txt ]; then python -m pip install -r requirements-no-torch.txt --no-cache-dir; fi
# Install ColorCorrectionPipeline without dependencies
RUN python -m pip install --no-deps ColorCorrectionPipeline --no-cache-dir

ENV PORT=5000
EXPOSE 5000

# Use a simple entrypoint that runs the Flask app
CMD ["python", "server_enhanced.py"]
