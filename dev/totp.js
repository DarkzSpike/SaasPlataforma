// Gera o código TOTP atual a partir do segredo de desenvolvimento (equivale ao app autenticador).
// Uso: node dev/totp.js
const crypto = require('crypto');
const secret = require('./dev-secrets.local.json').AdminTotpSecret;
const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
let bits = '';
for (const c of secret) bits += alphabet.indexOf(c).toString(2).padStart(5, '0');
const key = Buffer.from(bits.match(/.{8}/g).map(b => parseInt(b, 2)));
const step = Math.floor(Date.now() / 1000 / 30) + Number(process.argv[2] || 0);
const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(step));
const hm = crypto.createHmac('sha1', key).update(msg).digest();
const o = hm[hm.length - 1] & 15;
console.log(String((hm.readUInt32BE(o) & 0x7fffffff) % 1e6).padStart(6, '0'));
