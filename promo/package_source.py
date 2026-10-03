from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

root=Path(__file__).parent
out=root.parent/'bin/Novaina-Promo-Remotion.zip'
include=['README.md','package.json','package-lock.json','tsconfig.json','render.mjs','make_sfx.py','contact_sheet.py','finish.mjs','package_source.py','.gitignore']
with ZipFile(out,'w',ZIP_DEFLATED) as z:
    for name in include:
        z.write(root/name,'Novaina-Promo/'+name)
    for directory in ['src','licenses']:
        for p in (root/directory).rglob('*'):
            if p.is_file():z.write(p,'Novaina-Promo/'+p.relative_to(root).as_posix())
    for name in ['logo.png','steve.png','intro-sfx.wav']:
        z.write(root/'public'/name,'Novaina-Promo/public/'+name)
print(out, out.stat().st_size)
