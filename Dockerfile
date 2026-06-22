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
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Создаем виртуальное окружение Python и устанавливаем audio-separator для CPU
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
RUN pip install --no-cache-dir audio-separator[cpu]

# Копируем скомпилированное .NET приложение из первого этапа
COPY --from=build-env /app .

# Создаем папки для хранения данных
RUN mkdir -p instance wwwroot/uploads wwwroot/uploads/backgrounds wwwroot/output Models Fonts

# Создаем папку под модель и копируем файл Kim_Vocal_2.onnx внутрь образа
RUN mkdir -p /tmp/audio-separator-models
COPY ./Models/audio_models/Kim_Vocal_2.onnx /tmp/audio-separator-models/Kim_Vocal_2.onnx

# Копируем модель Whisper прямо в рабочую папку /app/Models внутри образа
COPY ./Models/ggml-medium.bin /app/Models/ggml-medium.bin

# Открываем порты для веб-сервера
EXPOSE 8080
EXPOSE 8081

# Запуск приложения
ENTRYPOINT ["dotnet", "KaraokePlatform.dll"]