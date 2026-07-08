import urllib.request
import urllib.parse
import os
import time
import ssl

context = ssl._create_unverified_context()

wheels = [
    # (url, local_dir)
    ("https://mirrors.aliyun.com/pytorch-wheels/cpu/torch-2.9.1%2Bcpu-cp310-cp310-manylinux_2_28_x86_64.whl", "wheels/transcription"),
    ("https://mirrors.aliyun.com/pytorch-wheels/cpu/torchvision-0.24.1%2Bcpu-cp310-cp310-manylinux_2_28_x86_64.whl", "wheels/transcription"),
    ("https://mirrors.aliyun.com/pytorch-wheels/cpu/torchaudio-2.9.1%2Bcpu-cp310-cp310-manylinux_2_28_x86_64.whl", "wheels/transcription"),
    ("https://mirrors.aliyun.com/pytorch-wheels/cpu/torch-2.9.1%2Bcpu-cp312-cp312-manylinux_2_28_x86_64.whl", "wheels/karaoke-app")
]

def download_file(url, local_dir):
    os.makedirs(local_dir, exist_ok=True)
    filename = urllib.parse.unquote(url.split("/")[-1])
    local_path = os.path.join(local_dir, filename)
    temp_path = local_path + ".tmp"
    
    if os.path.exists(local_path):
        print(f"File {filename} already exists. Skipping.")
        return

    print(f"\nStarting download: {filename}")
    
    max_retries = 10
    retry_delay = 5
    
    for attempt in range(1, max_retries + 1):
        try:
            headers = {
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
            }
            start_bytes = 0
            mode = "wb"
            
            if os.path.exists(temp_path):
                start_bytes = os.path.getsize(temp_path)
                headers["Range"] = f"bytes={start_bytes}-"
                mode = "ab"
                print(f"Resuming download from {start_bytes / 1024 / 1024:.2f} MB (Attempt {attempt}/{max_retries})...")
            else:
                print(f"New download (Attempt {attempt}/{max_retries})...")
                
            req = urllib.request.Request(url, headers=headers)
            with urllib.request.urlopen(req, context=context, timeout=30) as response:
                # If we asked for range but server ignored it and returned 200, overwrite
                if response.status == 200 and start_bytes > 0:
                    print("Server did not return partial content, restarting download from scratch...")
                    mode = "wb"
                    start_bytes = 0
                
                content_length = response.getheader("Content-Length")
                total_bytes = int(content_length) + start_bytes if content_length else None
                
                with open(temp_path, mode) as f:
                    block_size = 1024 * 1024 # 1 MB chunks
                    last_log_time = time.time()
                    downloaded_since_log = 0
                    
                    while True:
                        chunk = response.read(block_size)
                        if not chunk:
                            break
                        f.write(chunk)
                        downloaded_since_log += len(chunk)
                        
                        current_size = os.path.getsize(temp_path)
                        now = time.time()
                        if now - last_log_time >= 3:
                            speed = downloaded_since_log / (now - last_log_time) / 1024 / 1024
                            total_str = f"/{total_bytes / 1024 / 1024:.1f}" if total_bytes else ""
                            print(f"Downloaded: {current_size / 1024 / 1024:.1f}{total_str} MB | Speed: {speed:.2f} MB/s")
                            last_log_time = now
                            downloaded_since_log = 0
            
            os.rename(temp_path, local_path)
            print(f"Successfully finished: {filename}!")
            return
            
        except Exception as e:
            print(f"Error on attempt {attempt}: {e}")
            if attempt < max_retries:
                print(f"Waiting {retry_delay} seconds before retrying...")
                time.sleep(retry_delay)
            else:
                print(f"Failed to download {filename} after {max_retries} attempts.")
                raise e

if __name__ == "__main__":
    start_time = time.time()
    for url, local_dir in wheels:
        download_file(url, local_dir)
    print(f"\nAll downloads completed in {time.time() - start_time:.1f} seconds!")
