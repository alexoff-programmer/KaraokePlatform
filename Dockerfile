# ==========================================
# Этап 1: Сборка и публикация C# приложения
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /src

# Копируем файл проекта и восстанавливаем NuGet-пакеты
COPY KaraokePlatform.csproj ./
RUN dotnet restore KaraokePlatform.csproj

# Копируем остальные исходники и публикуем конкретный проект
COPY . ./
RUN dotnet publish KaraokePlatform.csproj -c Release -o /app

# ==========================================
# Этап 2: Финальный образ для запуска
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Устанавливаем системные зависимости: Python3, pip, ffmpeg и шрифты
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-pip \
    python3-venv \
    python3-dev \
    build-essential \
    ffmpeg \
    fontconfig \
    ca-certificates \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Создаем виртуальное окружение Python и устанавливаем audio-separator для CPU
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
ENV PIP_DEFAULT_TIMEOUT=1000
RUN pip config set global.index-url https://pypi-mirror.gitverse.ru/simple/ && \
    pip config set global.trusted-host pypi-mirror.gitverse.ru
RUN pip install --no-cache-dir --upgrade pip setuptools
COPY wheels/karaoke-app/ /wheels/
RUN pip install --no-cache-dir /wheels/*.whl && rm -rf /wheels
RUN pip install --no-cache-dir "torch>=2.9.0,<2.10.0" "audio-separator[cpu]"

# Копируем скомпилированное .NET приложение из первого этапа
COPY --from=build-env /app .

# Создаем папки для хранения данных
RUN mkdir -p instance wwwroot/uploads wwwroot/uploads/backgrounds wwwroot/output Models Fonts

# Создаем папку под модели и копируем файлы Kim_Vocal_2.onnx и BS-Roformer внутрь образа
RUN mkdir -p /tmp/audio-separator-models
COPY ./Models/audio_models/Kim_Vocal_2.onnx /tmp/audio-separator-models/Kim_Vocal_2.onnx
COPY ./Models/audio_models/model_bs_roformer_ep_317_sdr_12.9755.ckpt /tmp/audio-separator-models/model_bs_roformer_ep_317_sdr_12.9755.ckpt
COPY ./Models/audio_models/model_bs_roformer_ep_317_sdr_12.9755.yaml /tmp/audio-separator-models/model_bs_roformer_ep_317_sdr_12.9755.yaml

# ИСПРАВЛЕНО: Строка копирования ggml-medium.bin удалена. Модель теперь крутится в отдельном контейнере WhisperX!

# Открываем порты для веб-сервера
EXPOSE 8080
EXPOSE 8081

# Запуск приложения
ENTRYPOINT ["dotnet", "KaraokePlatform.dll"]