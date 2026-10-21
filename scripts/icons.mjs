import { mkdir, writeFile } from 'node:fs/promises'
await mkdir('build',{recursive:true})
const svg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><rect width="256" height="256" rx="54" fill="#152019"/><g fill="#a7df8b"><rect x="52" y="52" width="66" height="66" rx="5"/><rect x="138" y="52" width="66" height="66" rx="5" opacity=".55"/><rect x="52" y="138" width="66" height="66" rx="5" opacity=".75"/><rect x="138" y="138" width="66" height="66" rx="5"/></g></svg>'
await writeFile('build/icon.svg',svg)
const sizes=[32,48,64,128,256],images=[]
for(const size of sizes){
  const maskStride=Math.ceil(size/32)*4, pixels=Buffer.alloc(size*size*4),mask=Buffer.alloc(maskStride*size)
  for(let y=0;y<size;y++)for(let x=0;x<size;x++){
    const u=(x+.5)*256/size,v=(y+.5)*256/size
    const dx=Math.max(54-u,0,u-202),dy=Math.max(54-v,0,v-202)
    const transparent=dx*dx+dy*dy>54*54
    let c=[21,32,25]
    const column=u>=52&&u<118?0:u>=138&&u<204?1:-1,row=v>=52&&v<118?0:v>=138&&v<204?1:-1
    if(column>=0&&row>=0){const opacity=[[1,.55],[.75,1]][row][column];c=c.map((base,i)=>Math.round(base+([167,223,139][i]-base)*opacity))}
    const offset=((size-1-y)*size+x)*4
    pixels[offset]=c[2];pixels[offset+1]=c[1];pixels[offset+2]=c[0];pixels[offset+3]=transparent?0:255
    if(transparent)mask[(size-1-y)*maskStride+Math.floor(x/8)]|=1<<(7-x%8)
  }
  const header=Buffer.alloc(40);header.writeUInt32LE(40,0);header.writeInt32LE(size,4);header.writeInt32LE(size*2,8);header.writeUInt16LE(1,12);header.writeUInt16LE(32,14);header.writeUInt32LE(pixels.length+mask.length,20)
  images.push(Buffer.concat([header,pixels,mask]))
}
const directory=Buffer.alloc(6+16*sizes.length);directory.writeUInt16LE(1,2);directory.writeUInt16LE(sizes.length,4);let offset=directory.length
sizes.forEach((size,i)=>{const p=6+i*16;directory[p]=size===256?0:size;directory[p+1]=directory[p];directory.writeUInt16LE(1,p+4);directory.writeUInt16LE(32,p+6);directory.writeUInt32LE(images[i].length,p+8);directory.writeUInt32LE(offset,p+12);offset+=images[i].length})
await writeFile('build/icon.ico',Buffer.concat([directory,...images]))
