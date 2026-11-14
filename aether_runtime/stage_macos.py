#!/usr/bin/env python3
"""Stage architecture-correct native dependencies beside the NativeAOT library."""
import os, pathlib, shutil, subprocess, sys
out=pathlib.Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=True)
prefix=subprocess.check_output(['brew','--prefix'],text=True).strip()
queue=[pathlib.Path(prefix)/'lib/libfreetype.dylib']
openal=pathlib.Path(subprocess.check_output(['brew','--prefix','openal-soft'],text=True).strip())/'lib/libopenal.dylib'
if openal.exists(): queue.append(openal)
seen=set()
while queue:
    src=queue.pop()
    if src.name in seen: continue
    seen.add(src.name)
    dst=out/src.name
    shutil.copy2(src,dst);os.chmod(dst,0o755)
    subprocess.run(['install_name_tool','-id','@loader_path/'+dst.name,str(dst)],check=True)
    dependencies=subprocess.check_output(['otool','-L',str(src)],text=True).splitlines()[2:]
    for line in dependencies:
        dep=line.strip().split(' (',1)[0]
        if dep.startswith(prefix) or dep.startswith('@rpath/'):
            dependency=pathlib.Path(dep) if dep.startswith(prefix) else pathlib.Path(prefix)/'lib'/pathlib.Path(dep).name
            queue.append(dependency)
            subprocess.run(['install_name_tool','-change',dep,'@loader_path/'+pathlib.Path(dep).name,str(dst)],check=True)
    subprocess.run(['codesign','--force','--sign','-',str(dst)],check=True,stdout=subprocess.DEVNULL)
