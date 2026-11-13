const fs=require("fs"),path=require("path");const m=JSON.parse(fs.readFileSync("sea/meta.json"));
const set=new Map();for(const k of Object.keys(m.inputs)){const mm=k.match(/node_modules\/((?:@[^/]+\/)?[^/]+)/);if(mm&&!set.has(mm[1])){let lic="?";try{lic=JSON.parse(fs.readFileSync(path.join("node_modules",mm[1],"package.json"))).license}catch{}set.set(mm[1],lic)}}
console.log([...set].map(([n,l])=>`- ${n} – ${l}`).join("\n"));
