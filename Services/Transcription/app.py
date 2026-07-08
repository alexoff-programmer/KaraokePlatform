import os
import traceback
from fastapi import FastAPI, UploadFile, File, Form, HTTPException
import whisperx
import torch
import gc

app = FastAPI(title="Karaoke Transcription Service (WhisperX)")

# Жестко переводим в CPU режим
device = "cpu"
# На CPU float16 ЗАПРЕЩЕН, используем int8 (быстро и мало памяти) или float32
compute_type = "int8" 

whisper_model = None
align_models = {}

HF_TOKEN = os.getenv("HF_TOKEN", "")
MODEL_NAME = os.getenv("WHISPER_MODEL", "base")

@app.on_event("startup")
def load_models():
    global whisper_model
    print(f"Загрузка модели WhisperX ({MODEL_NAME}) на устройство: {device}...")
    # Инициализируем модель с поддержкой процессорных вычислений int8
    whisper_model = whisperx.load_model(MODEL_NAME, device, compute_type=compute_type)
    print("Модель WhisperX успешно загружена.")

@app.post("/transcribe")
async def transcribe_audio(
    file: UploadFile = File(...),
    language: str = Form("ru") # По умолчанию ставим русский, если бэк промолчит
):
    global align_models
    
    temp_file_path = f"temp_{file.filename}"
    with open(temp_file_path, "wb") as buffer:
        buffer.write(await file.read())
        
    try:
        audio = whisperx.load_audio(temp_file_path)
        
        # Если передан язык "auto", используем автодетект (передаем None в WhisperX)
        transcribe_language = None if language == "auto" else language
        
        vad_options = {
            "vad_onset": 0.3,   # Снижаем порог начала речи (по умолчанию 0.5)
            "vad_offset": 0.2,  # Снижаем порог окончания
            "max_speech_duration_s": 20
        }
        
        result = whisper_model.transcribe(
            audio, 
            language=transcribe_language, 
            batch_size=4, 
            asr_options={"condition_on_previous_text": False},
            vad_options=vad_options
        ) # Уменьшаем batch_size для CPU
        
        # Определяем язык для выравнивания (если был автодетект, берем определенный моделью язык)
        detected_language = result.get("language")
        align_language = detected_language if language == "auto" else language
        
        # Если язык не определился или пустой, ставим по умолчанию "ru"
        if not align_language:
            align_language = "ru"
            
        if align_language not in align_models:
            model_name_override = None
            if align_language == "ru":
                model_name_override = "jonatasgrosman/wav2vec2-large-xlsr-53-russian"
            model_a, metadata = whisperx.load_align_model(language_code=align_language, device=device, model_name=model_name_override)
            align_models[align_language] = (model_a, metadata)
        else:
            model_a, metadata = align_models[align_language]
            
        aligned_result = whisperx.align(
            result["segments"], 
            model_a, 
            metadata, 
            audio, 
            device, 
            return_char_alignments=False
        )
        
        output_words = []
        for segment in aligned_result["segments"]:
            if "words" not in segment:
                continue
            for word_data in segment["words"]:
                if "start" in word_data and "end" in word_data:
                    output_words.append({
                        "word": word_data["word"],
                        "start_ms": int(word_data["start"] * 1000),
                        "end_ms": int(word_data["end"] * 1000)
                    })
                    
        return {"words": output_words}

    except Exception as e:
        # Если упадет внутри, FastAPI выведет точный трейс в логи докера
        print("--- ПОДРОБНАЯ ОШИБКА ---")
        traceback.print_exc() # Выведет полный путь к ошибке в лог докера
        print(f"Критическая ошибка при обработке: {str(e)}")
        raise HTTPException(status_code=500, detail=str(e))
    finally:
        if os.path.exists(temp_file_path):
            os.remove(temp_file_path)
        gc.collect()