# ==========================================
# Этап 1: Сборка и публикация C# приложения
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /src

# Копируем файл проекта и восстанавливаем NuGet-пакеты
COPY KaraokePlatform.csproj ./
RUN dotnet restore KaraokePlatform.csproj

# Копируем остальные исходники и публикуем конкретный проект, а не решение целиком
COPY . ./
RUN dotnet publish KaraokePlatform.csproj -c Release -o /app

# ==========================================
# Этап 2: Финальный образ для запуска
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Устанавливаем системные зависимости: Python3, pip, ffmpeg и шрифты для субтитров
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

# Создаем виртуальное окружение Python и устанавливаем audio-separator
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
RUN pip install --no-cache-dir audio-separator[cpu]

# Копируем скомпилированное .NET приложение из первого этапа
COPY --from=build-env /app .

# Создаем папки для хранения данных, чтобы они не затирались при перезапуске
RUN mkdir -p instance wwwroot/uploads wwwroot/uploads/backgrounds wwwroot/output Models Fonts

# Открываем порты для веб-сервера (8080 — дефолтный порт в .NET 8+)
EXPOSE 8080
EXPOSE 8081

# Запуск приложения
ENTRYPOINT ["dotnet", "KaraokePlatform.dll"]