#!/usr/bin/env python3
"""Compile portable shader resources; requires glslangValidator and spirv-cross."""
from pathlib import Path
import subprocess
root = Path(__file__).resolve().parent.parent
source = root / 'src/NitroSharp.Rebuild/Graphics/Shaders'
out = root / 'aether_runtime/shaders'
out.mkdir(exist_ok=True)
for shader in sorted(source.iterdir()):
    if shader.suffix not in ('.vert', '.frag'): continue
    stage = 'vertex' if shader.suffix == '.vert' else 'fragment'
    base = out / f'{shader.stem}-{stage}'
    spv = str(base) + '.450.glsl.spv'
    subprocess.run(['glslangValidator', '-V', str(shader), '-o', spv], check=True)
    subprocess.run(['spirv-cross', spv, '--msl', '--msl-version', '20100', '--output', str(base)+'.metal'], check=True)
    subprocess.run(['spirv-cross', spv, '--hlsl', '--shader-model', '50', '--output', str(base)+'.hlsl'], check=True)

# Veldrid's improved binding model numbers each resource kind across descriptor
# sets, including layouts used by the other shader stage. spirv-cross's default
# Metal numbering starts at zero independently in every stage, so rebase it.
import json,re
for vertex in sorted(source.glob('*.vert')):
    name=vertex.stem
    modules={}
    resources={}
    kinds={'ubos':'buffer','ssbos':'buffer','separate_images':'texture','separate_samplers':'sampler'}
    for stage in ('vertex','fragment'):
        base=out/f'{name}-{stage}'
        reflection=json.loads(subprocess.check_output(['spirv-cross',str(base)+'.450.glsl.spv','--reflect'],text=True))
        modules[stage]=reflection
        for category,kind in kinds.items():
            for resource in reflection.get(category,[]):
                resources[(resource['set'],resource['binding'])]=(kind,resource)
    counts={'buffer':0,'texture':0,'sampler':0}
    indices={}
    for key,(kind,resource) in sorted(resources.items()):
        indices[key]=counts[kind];counts[kind]+=1
    for stage,reflection in modules.items():
        path=out/f'{name}-{stage}.metal'
        text=path.read_text()
        for category,kind in kinds.items():
            for resource in reflection.get(category,[]):
                index=indices[(resource['set'],resource['binding'])]
                if category in ('ubos','ssbos'):
                    pattern=r'((?:constant|device)\s+'+re.escape(resource['name'])+r'\s*&?\s*\w+\s*\[\[buffer\()\d+(\)\]\])'
                else:
                    pattern=r'(\b'+re.escape(resource['name'])+r'\s*\[\['+kind+r'\()\d+(\)\]\])'
                text,n=re.subn(pattern,lambda match:match[1]+str(index)+match[2],text)
                if n!=1: raise RuntimeError(f'Could not bind {name} {stage} {resource["name"]}: {n}')
        path.write_text(text)
        hlsl_path=out/f'{name}-{stage}.hlsl'
        hlsl=hlsl_path.read_text()
        for category,kind in kinds.items():
            for resource in reflection.get(category,[]):
                index=indices[(resource['set'],resource['binding'])]
                register={'ubos':'b','ssbos':'t','separate_images':'t','separate_samplers':'s'}[category]
                pattern=r'(\b'+re.escape(resource['name'])+r'\s*:\s*register\('+register+r')\d+(\))'
                hlsl,n=re.subn(pattern,lambda match:match[1]+str(index)+match[2],hlsl)
                if n!=1: raise RuntimeError(f'Could not bind HLSL {name} {stage} {resource["name"]}: {n}')
        hlsl_path.write_text(hlsl)

# Keep generated text deterministic and clean for source control.
for path in out.iterdir():
    if path.suffix in ('.metal', '.hlsl'):
        path.write_text('\n'.join(line.rstrip() for line in path.read_text().splitlines()).rstrip() + '\n')
