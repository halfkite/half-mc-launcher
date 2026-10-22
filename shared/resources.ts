export type ResourceKind='mod'|'resourcepack'|'shader'
export type ResourceSource='modrinth'|'curseforge'
export type ResourceProject={id:string;source:ResourceSource;name:string;summary:string;downloads:number;author:string;icon?:string}
export type ResourceVersion={id:string;projectId:string;source:ResourceSource;name:string;filename:string;url:string;hash:string;algorithm:'sha512'|'sha1'|'md5';size:number;games:string[];loaders:string[];dependencies:{projectId?:string;versionId?:string}[];kind:ResourceKind}
export type ModFile={file:string;name:string;version:string;enabled:boolean;size:number;issue?:string;icon?:string}
