import { materializeNativeHelper } from "../host.ts";

console.log(JSON.stringify(await materializeNativeHelper(), null, 2));
