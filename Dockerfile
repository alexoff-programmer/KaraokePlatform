# ==========================================
# 1. Этап сборки (Build Stage)
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Используем кеширование слоев Docker: сначала копируем и восстанавливаем зависимости
COPY ["KaraokePlatform.csproj", "./"]
RUN dotnet restore "./KaraokePlatform.csproj"

# Копируем остальные исходники и собираем оптимизированный Release
COPY . .
RUN dotnet publish "KaraokePlatform.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ==========================================
# 2. Этап запуска (Runtime Stage)
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Окружение для правильной работы кодировок (важно для Whisper и FFmpeg при обработке текста)
ENV LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    ASPNETCORE_URLS=http://+:8080

# Обновляем пакетную базу и ставим мультимедийные утилиты
# libgomp1 жизненно необходим Whisper.net для распараллеливания вычислений на Linux
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Копируем скомпилированное приложение из предыдущего этапа
COPY --from=build /app/publish .

# Запуск приложения
ENTRYPOINT ["dotnet", "KaraokePlatform.dll"]