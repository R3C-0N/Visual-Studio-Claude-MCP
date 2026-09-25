#!/usr/bin/env node
/**
 * Relais stdio -> HTTP pour le serveur MCP de Visual Studio.
 *
 * Claude Code lance ce relais comme un serveur MCP stdio : de son point de vue, le serveur est
 * toujours connecte, qu'une instance de Visual Studio soit ouverte ou non. Chaque requete est
 * relayee vers le hub HTTP (http://127.0.0.1:5230/mcp par defaut) au moment ou elle arrive.
 *
 * Pourquoi : le hub n'ecoute que tant qu'une instance de Visual Studio est ouverte, et change de
 * processus quand l'instance hub se ferme (reprise en moins de dix secondes). Claude Code ne
 * retente une connexion HTTP que quelques fois, puis marque le serveur en echec jusqu'a un
 * /mcp > Reconnect manuel. Le relais, lui, attend Visual Studio aussi longtemps qu'il le faut.
 *
 * Comportement :
 *  - initialize et ping sont repondus localement, sans Visual Studio ;
 *  - tools/list interroge Visual Studio et garde la liste en cache sur disque ; sans Visual
 *    Studio, la derniere liste connue est servie ;
 *  - tools/call attend Visual Studio jusqu'a CLAUDE_VS_MCP_WAIT_MS (30 s par defaut, de quoi
 *    couvrir une reprise de hub), puis renvoie un resultat d'outil en erreur, lisible ;
 *  - quand Visual Studio redevient joignable et que sa liste d'outils differe de celle servie,
 *    le relais envoie notifications/tools/list_changed pour que Claude Code la recharge.
 *
 * Aucune dependance : Node 18 ou plus suffit. Rien n'est ecrit sur stdout hors JSON-RPC ; le
 * journal part sur stderr, que Claude Code conserve dans ses logs MCP.
 *
 * Variables d'environnement :
 *  - CLAUDE_VS_MCP_URL       URL du hub (defaut : http://127.0.0.1:<port>/mcp)
 *  - CLAUDE_VS_MCP_HUB_PORT  port du hub quand l'URL n'est pas donnee (defaut : 5230)
 *  - CLAUDE_VS_MCP_TOKEN     jeton (defaut : relu a chaque requete dans %APPDATA%\claude-vs-mcp\token)
 *  - CLAUDE_VS_MCP_WAIT_MS   attente maximale de Visual Studio pour un appel d'outil (defaut : 30000)
 */

import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { randomUUID } from 'node:crypto';

const hubPort = Number(process.env.CLAUDE_VS_MCP_HUB_PORT) || 5230;
const hubUrl = new URL(process.env.CLAUDE_VS_MCP_URL || `http://127.0.0.1:${hubPort}/mcp`);
const waitMs = Number(process.env.CLAUDE_VS_MCP_WAIT_MS) || 30000;

const appData = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
const tokenFile = path.join(appData, 'claude-vs-mcp', 'token');
const cacheDir = path.join(process.env.LOCALAPPDATA || appData, 'claude-vs-mcp');
const cacheFile = path.join(cacheDir, 'relay-cache.json');

/** Delai court pour les requetes de service (liste d'outils, sonde) : Visual Studio est local. */
const probeTimeoutMs = 3000;

/** Intervalle de la sonde qui detecte le retour de Visual Studio. */
const probeIntervalMs = 5000;

/**
 * Identifiant de session stable pour toute la vie du relais : use_instance est memorise par
 * session cote Visual Studio, il survit ainsi aux reconnexions du relais.
 */
const sessionId = `relay-${randomUUID()}`;

const defaultInstructions =
    'Pilote une ou plusieurs instances de Visual Studio ouvertes sur cette machine. ' +
    'Appeler list_instances quand plusieurs solutions peuvent etre ouvertes, puis use_instance ' +
    'pour fixer la cible ; sinon les outils s\'appliquent a l\'unique instance ouverte. ' +
    'Pour une fenetre sans outil dedie (Explorateur de tests...), ui_snapshot donne l\'arbre des controles ' +
    'avec des ids, ui_action agit dessus ; execute_command lance une commande nommee de Visual Studio.';

let cache = loadCache();
let servedToolsJson = null;   // liste d'outils telle que Claude Code l'a recue en dernier
let clientInitialized = false;
let upstreamUp = false;

function log(message) {
    process.stderr.write(`[vs-mcp-relay] ${new Date().toISOString()} ${message}\n`);
}

function loadCache() {
    try {
        return JSON.parse(fs.readFileSync(cacheFile, 'utf8'));
    } catch {
        return {};
    }
}

function saveCache() {
    try {
        fs.mkdirSync(cacheDir, { recursive: true });
        fs.writeFileSync(cacheFile, JSON.stringify(cache, null, 2), 'utf8');
    } catch (error) {
        log(`cache non ecrit : ${error.message}`);
    }
}

function readToken() {
    if (process.env.CLAUDE_VS_MCP_TOKEN) return process.env.CLAUDE_VS_MCP_TOKEN.trim();
    try {
        return fs.readFileSync(tokenFile, 'utf8').trim();
    } catch {
        return null;
    }
}

function send(message) {
    process.stdout.write(JSON.stringify(message) + '\n');
}

function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

/** Erreur qui signifie « personne n'ecoute encore » : la seule qu'il vaut la peine d'attendre. */
function isUnreachable(error) {
    return ['ECONNREFUSED', 'ECONNRESET', 'EPIPE', 'ETIMEDOUT', 'EHOSTUNREACH', 'ENETUNREACH']
        .includes(error?.code) || error?.unreachable === true;
}

/**
 * Envoie une requete JSON-RPC au hub et renvoie sa reponse JSON (ou null pour un 202).
 * Pas de delai par defaut : un build peut durer. timeoutMs ne sert qu'aux requetes de service.
 */
function postUpstream(body, timeoutMs) {
    return new Promise((resolve, reject) => {
        const token = readToken();
        if (!token) {
            const error = new Error(`jeton introuvable dans ${tokenFile} : ouvrir Visual Studio avec l'extension installee`);
            error.unreachable = true;
            reject(error);
            return;
        }

        const payload = Buffer.from(JSON.stringify(body), 'utf8');
        const request = http.request(hubUrl, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Accept': 'application/json, text/event-stream',
                'Content-Length': payload.length,
                'Authorization': `Bearer ${token}`,
                'Mcp-Session-Id': sessionId
            }
        }, response => {
            const chunks = [];
            response.on('data', chunk => chunks.push(chunk));
            response.on('end', () => {
                const text = Buffer.concat(chunks).toString('utf8');
                if (response.statusCode === 202 || text.trim() === '') {
                    resolve(null);
                    return;
                }
                try {
                    resolve(JSON.parse(text));
                } catch {
                    reject(new Error(`reponse HTTP ${response.statusCode} illisible : ${text.slice(0, 200)}`));
                }
            });
            response.on('error', reject);
        });

        request.on('error', reject);
        if (timeoutMs) {
            request.setTimeout(timeoutMs, () => {
                const error = new Error(`pas de reponse en ${timeoutMs} ms`);
                error.code = 'ETIMEDOUT';
                request.destroy(error);
            });
        }
        request.end(payload);
    });
}

/**
 * Relaie une requete en attendant Visual Studio jusqu'a budgetMs s'il n'est pas joignable.
 * Une erreur autre qu'une absence (jeton refuse, reponse illisible) n'est pas reessayee.
 */
async function postWithWait(body, budgetMs, timeoutMs) {
    const started = Date.now();
    let delay = 250;

    for (;;) {
        try {
            const reply = await postUpstream(body, timeoutMs);
            markUp();
            return reply;
        } catch (error) {
            if (!isUnreachable(error) || Date.now() - started + delay > budgetMs) {
                if (isUnreachable(error)) markDown(error);
                throw error;
            }
            await sleep(delay);
            delay = Math.min(delay * 2, 2000);
        }
    }
}

function markUp() {
    if (!upstreamUp) log(`Visual Studio joignable sur ${hubUrl.href}`);
    upstreamUp = true;
}

function markDown(error) {
    if (upstreamUp) log(`Visual Studio injoignable (${error.code || error.message})`);
    upstreamUp = false;
}

/** Initialise une session cote Visual Studio : l'extension ne l'exige pas, mais c'est le protocole. */
async function upstreamInitialize(timeoutMs) {
    const reply = await postUpstream({
        jsonrpc: '2.0',
        id: `relay-init-${Date.now()}`,
        method: 'initialize',
        params: {
            protocolVersion: cache.protocolVersion || '2025-06-18',
            capabilities: {},
            clientInfo: { name: 'vs-mcp-relay', version: '1.0.0' }
        }
    }, timeoutMs);

    const instructions = reply?.result?.instructions;
    if (instructions && instructions !== cache.instructions) {
        cache.instructions = instructions;
        saveCache();
    }
}

/** Recupere la liste d'outils de Visual Studio et la met en cache. */
async function fetchTools(timeoutMs) {
    const reply = await postUpstream({
        jsonrpc: '2.0', id: `relay-tools-${Date.now()}`, method: 'tools/list', params: {}
    }, timeoutMs);

    const tools = reply?.result?.tools;
    if (!Array.isArray(tools)) throw new Error('tools/list sans liste d\'outils');

    markUp();
    if (JSON.stringify(tools) !== JSON.stringify(cache.tools)) {
        cache.tools = tools;
        saveCache();
    }
    return tools;
}

/** Resultat d'outil en erreur : Claude Code l'affiche comme la reponse de l'outil. */
function toolError(id, text) {
    return { jsonrpc: '2.0', id, result: { content: [{ type: 'text', text }], isError: true } };
}

async function handle(message) {
    const { id, method } = message;
    const isNotification = id === undefined || id === null;

    if (isNotification) {
        if (method === 'notifications/initialized') {
            clientInitialized = true;
            // Si Visual Studio est deja la, la liste servie est verifiee tout de suite.
            probe().catch(() => {});
        }
        // Les autres notifications (cancelled...) sont ignorees par l'extension : inutile de relayer.
        return;
    }

    switch (method) {
        case 'initialize': {
            const requested = message.params?.protocolVersion;
            if (requested && requested !== cache.protocolVersion) {
                cache.protocolVersion = requested;
                saveCache();
            }
            send({
                jsonrpc: '2.0', id,
                result: {
                    protocolVersion: requested || '2025-06-18',
                    capabilities: { tools: { listChanged: true } },
                    serverInfo: { name: 'visual-studio', version: 'relay' },
                    instructions: cache.instructions || defaultInstructions
                }
            });
            return;
        }

        case 'ping':
            send({ jsonrpc: '2.0', id, result: {} });
            return;

        case 'tools/list': {
            let tools;
            try {
                tools = await fetchTools(probeTimeoutMs);
            } catch (error) {
                markDown(error);
                tools = cache.tools || [];
                log(`tools/list servi depuis le cache (${tools.length} outils) : ${error.message}`);
            }
            servedToolsJson = JSON.stringify(tools);
            send({ jsonrpc: '2.0', id, result: { tools } });
            return;
        }

        case 'tools/call': {
            try {
                const reply = await postWithWait(message, waitMs);
                send(reply ?? toolError(id, 'Visual Studio a accepte l\'appel sans renvoyer de resultat.'));
            } catch (error) {
                send(toolError(id, isUnreachable(error)
                    ? `Visual Studio n'est pas joignable sur ${hubUrl.href} depuis ${Math.round(waitMs / 1000)} s ` +
                      `(${error.code || error.message}). Ouvrir Visual Studio avec la solution, puis relancer l'appel : ` +
                      'le relais se reconnecte tout seul, sans /mcp.'
                    : `Echec du relais vers Visual Studio : ${error.message}`));
            }
            return;
        }

        default: {
            // Methode non geree localement : relayee telle quelle, sans attente.
            try {
                const reply = await postUpstream(message, probeTimeoutMs);
                if (reply) {
                    send(reply);
                    return;
                }
            } catch (error) {
                markDown(error);
            }
            send({ jsonrpc: '2.0', id, error: { code: -32601, message: `Methode non disponible : ${method}` } });
        }
    }
}

/**
 * Sonde periodique : detecte le retour de Visual Studio et previent Claude Code quand la liste
 * d'outils qu'il detient n'est plus la bonne (typiquement : servie vide ou depuis le cache).
 */
async function probe() {
    try {
        const wasUp = upstreamUp;
        if (!wasUp) await upstreamInitialize(probeTimeoutMs);
        const tools = await fetchTools(probeTimeoutMs);

        if (clientInitialized && servedToolsJson !== null && JSON.stringify(tools) !== servedToolsJson) {
            log('liste d\'outils changee : notifications/tools/list_changed envoye');
            servedToolsJson = JSON.stringify(tools);
            send({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' });
        }
    } catch (error) {
        markDown(error);
    }
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

input.on('line', line => {
    if (!line.trim()) return;

    let message;
    try {
        message = JSON.parse(line);
    } catch {
        send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'JSON invalide' } });
        return;
    }

    // Chaque requete est traitee a part : un build long ne doit pas bloquer les suivantes.
    const messages = Array.isArray(message) ? message : [message];
    for (const item of messages) {
        handle(item).catch(error => {
            log(`erreur sur ${item?.method} : ${error.stack || error.message}`);
            if (item?.id !== undefined && item?.id !== null) {
                send({ jsonrpc: '2.0', id: item.id, error: { code: -32603, message: error.message } });
            }
        });
    }
});

input.on('close', () => process.exit(0));

const timer = setInterval(() => probe().catch(() => {}), probeIntervalMs);
timer.unref();

log(`demarre, hub ${hubUrl.href}, session ${sessionId}`);
