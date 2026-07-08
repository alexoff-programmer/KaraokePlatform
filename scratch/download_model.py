import urllib.request
import ssl
import sys

# Отключаем проверку SSL если есть проблемы с сертификатами локально
context = ssl._create_unverified_context()

url = "https://hf-mirror.com/KitsuneX07/Music_Source_Sepetration_Models/resolve/main/vocal_models/model_bs_roformer_ep_317_sdr_12.9755.ckpt"
dest = "c:/CSHARP/KaraokePlatform/Models/audio_models/model_bs_roformer_ep_317_sdr_12.9755.ckpt"

class ProgressTracker:
    def __init__(self):
        self.last_percent = -1

    def __call__(self, block_num, block_size, total_size):
        read_so_far = block_num * block_size
        if total_size > 0:
            percent = int(read_so_far * 100 / total_size)
            if percent != self.last_percent and percent % 5 == 0:
                print(f"Progress: {percent}% ({read_so_far // (1024*1024)}MB / {total_size // (1024*1024)}MB)")
                sys.stdout.flush()
                self.last_percent = percent

print(f"Starting download from {url} to {dest}...")
sys.stdout.flush()

try:
    opener = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context))
    urllib.request.install_opener(opener)
    urllib.request.urlretrieve(url, dest, reporthook=ProgressTracker())
    print("Download complete!")
except Exception as e:
    print(f"Error occurred: {e}")
    sys.exit(1)
