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

# Устанавливаем системные зависимости: ffmpeg, libgomp1, шрифты и Python3
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    libgomp1 \
    fontconfig \
    fonts-open-sans \
    fonts-noto-core \
    ca-certificates \
    python3 \
    python3-pip \
    python3-venv \
    && fc-cache -fv \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Создаем виртуальное окружение Python и устанавливаем audio-separator
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
RUN pip install --no-cache-dir --upgrade pip && \
    pip install --no-cache-dir audio-separator[cpu]

# Копируем скомпилированное .NET приложение из первого этапа
COPY --from=build-env /app .

# Создаем папки для хранения данных
RUN mkdir -p instance wwwroot/uploads wwwroot/uploads/backgrounds wwwroot/output Models Fonts

# Создаем папку под модели
RUN mkdir -p Models/audio_models

# Открываем порты для веб-сервера
EXPOSE 8080
EXPOSE 8081

# Запуск приложения
ENTRYPOINT ["dotnet", "KaraokePlatform.dll"]