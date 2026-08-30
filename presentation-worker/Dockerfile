FROM python:3.12.11-slim-bookworm

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HOME=/tmp/presentation-home

RUN apt-get update \
    && apt-get install --no-install-recommends -y \
        fonts-noto-cjk \
        libreoffice-impress \
        libreoffice-writer \
        nodejs \
        npm \
        poppler-utils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir --requirement requirements.txt
RUN npm install --global pptxgenjs@4.0.1

COPY app.py .

RUN useradd --create-home --uid 10001 worker \
    && mkdir -p /tmp/presentation-home /workspace \
    && chown -R worker:worker /app /tmp/presentation-home /workspace

USER worker
ENV PRESENTATION_WORKSPACE=/workspace
ENV NODE_PATH=/usr/local/lib/node_modules
EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log", "--limit-concurrency", "4"]
