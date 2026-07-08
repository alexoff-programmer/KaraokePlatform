import urllib.request
import ssl
import re

context = ssl._create_unverified_context()
url = "https://mirrors.aliyun.com/pytorch-wheels/cpu/"
print("Fetching index...")
try:
    html = urllib.request.urlopen(url, context=context).read().decode('utf-8')
    print("Fetched successfully. Analyzing...")
except Exception as e:
    print("Error:", e)
    exit(1)

# Find all links
links = re.findall(r'href="([^"]+\.whl)"', html)
print(f"Found {len(links)} total wheels.")

patterns = {
    "torch_310": re.compile(r'torch-\d+\.\d+\.\d+.*-cp310-cp310-manylinux.*_x86_64.whl'),
    "torchvision_310": re.compile(r'torchvision-\d+\.\d+\.\d+.*-cp310-cp310-manylinux.*_x86_64.whl'),
    "torchaudio_310": re.compile(r'torchaudio-\d+\.\d+\.\d+.*-cp310-cp310-manylinux.*_x86_64.whl'),
    "torch_312": re.compile(r'torch-\d+\.\d+\.\d+.*-cp312-cp312-manylinux.*_x86_64.whl')
}

found = {k: [] for k in patterns}
for link in links:
    # Decode URL encoding like %2B to +
    decoded_link = urllib.parse.unquote(link)
    for name, pat in patterns.items():
        if pat.search(decoded_link):
            found[name].append((decoded_link, link))

for name, items in found.items():
    print(f"\n--- {name} (Total: {len(items)}) ---")
    # Sort by version number
    # Simple sort will do since version numbers are formatted nicely
    items.sort(key=lambda x: x[0], reverse=True)
    for i in items[:3]:
        print(f"Decoded: {i[0]} -> Raw link: {i[1]}")
