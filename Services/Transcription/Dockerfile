# Используем легковесный Python на базе Debian
FROM python:3.10-slim

WORKDIR /app

ENV PIP_DEFAULT_TIMEOUT=1000


# 1. Объединяем обновление и установку, чтобы гарантировать разрыв битого кэша Docker
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    git \
    build-essential \
    ca-certificates \
    && update-ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir --upgrade pip setuptools

# 2. Копируем локальные колеса и устанавливаем torch CPU
COPY wheels/ /wheels/
RUN pip install --no-cache-dir /wheels/*.whl && rm -rf /wheels

# 3. Фиксируем стабильные версии numpy и scipy, чтобы избежать бесконечной компиляции
RUN pip install --no-cache-dir "numpy<2.0.0" "scipy<1.13.0"

# 4. Ставим веб-сервер и окружение
RUN pip install --no-cache-dir fastapi uvicorn python-multipart setuptools

# 5. Ставим WhisperX из репозитория (теперь git точно на месте)
RUN pip install --no-cache-dir git+https://github.com/m-bain/whisperX.git

# Копируем исходный код
COPY . .

ENV WHISPER_MODEL=base
ENV HF_TOKEN=""

EXPOSE 8000

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]