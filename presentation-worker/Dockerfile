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
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir --requirement requirements.txt

COPY app.py .
COPY tests ./tests

RUN useradd --create-home --uid 10001 worker \
    && mkdir -p /tmp/presentation-home \
    && chown -R worker:worker /app /tmp/presentation-home

USER worker
EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log", "--limit-concurrency", "4"]
