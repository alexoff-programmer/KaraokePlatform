import urllib.request
import ssl
import re
import urllib.parse

context = ssl._create_unverified_context()
url = "https://mirrors.aliyun.com/pytorch-wheels/cpu/"
print("Fetching...")
try:
    html = urllib.request.urlopen(url, context=context).read().decode('utf-8')
    print("Parsing...")
    links = re.findall(r'href="([^"]+\.whl)"', html)
    pat = re.compile(r'torchvision-\d+\.\d+\.\d+.*-cp312-cp312-manylinux.*_x86_64.whl')
    found = []
    for link in links:
        decoded = urllib.parse.unquote(link)
        if pat.search(decoded):
            found.append(decoded)
    found.sort(reverse=True)
    for f in found[:10]:
        print(f)
except Exception as e:
    print("Error:", e)
