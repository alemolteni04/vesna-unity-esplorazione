"""
AI Bridge (artefatti) - traduce un comando in linguaggio naturale in un
artefatto valido (building_artifacts.json) e scrive il risultato in target.json,
che verra' letto da JaCaMo.

Identico ad ai_bridge.py, ma la lista dei target validi viene presa dagli
artefatti scoperti (file *_artifacts.json) invece che dai nodi del grafo.

Uso:
    python ai_bridge_artifacts.py
    > portami all'artefatto dell'ufficio5
    -> scritto target.json con {"target": "Artefatto ufficio5"}
"""

import json
import os
import difflib
import re
import google.generativeai as genai

genai.configure(api_key=os.environ["GEMINI_API_KEY"])
gemini_model = genai.GenerativeModel("gemini-2.5-flash")

# --- Configurazione percorsi --------------------------------------------
GRAPH_DIR = os.path.join(
    os.path.expandvars("%USERPROFILE%"),
    "AppData", "LocalLow", "DefaultCompany", "JaCaMoIntegration", "graph_snapshots"
)
TARGET_JSON = os.path.join(GRAPH_DIR, "target.json")


def find_artifacts_json():
    """Trova il file *_artifacts.json nella cartella, prendendo il piu' recente
    per data di modifica."""
    candidates = []
    for fname in os.listdir(GRAPH_DIR):
        if fname.lower().endswith("_artifacts.json"):
            full = os.path.join(GRAPH_DIR, fname)
            candidates.append(full)

    if not candidates:
        raise FileNotFoundError("Nessun file *_artifacts.json trovato in " + GRAPH_DIR)

    candidates.sort(key=os.path.getmtime, reverse=True)
    return candidates[0]


def load_valid_artifacts():
    """Legge il file degli artefatti e ritorna:
       - la lista dei nomi validi (artifactId)
       - un dizionario {artifactId: roomId} usato come hint per l'LLM."""
    artifacts_json = find_artifacts_json()
    print(f"File artefatti trovato: {artifacts_json}")

    with open(artifacts_json, "r", encoding="utf-8") as f:
        data = json.load(f)

    names = []
    rooms = {}

    for a in data.get("artifacts", []):
        art_id = a.get("artifactId")
        if not art_id:
            continue
        names.append(art_id)
        rooms[art_id] = a.get("roomId", "")

    # dedup mantenendo l'ordine
    seen = set()
    unique = []
    for n in names:
        if n not in seen:
            seen.add(n)
            unique.append(n)

    return sorted(unique), rooms


def _local_match(user_input: str, valid_names: list[str]) -> str | None:
    """Fuzzy matching locale di fallback (no API)."""
    text = user_input.lower()
    text_norm = re.sub(r"[^a-z0-9]", "", text)

    best_match = None
    best_score = 0.0

    for name in valid_names:
        name_lower = name.lower()
        name_compact = re.sub(r"[^a-z0-9]", "", name_lower)

        if name_compact in text_norm:
            return name

        parts = re.findall(r"[a-z]+|\d+", name_lower)
        if len(parts) > 1 and all(p in text for p in parts):
            return name

        score = difflib.SequenceMatcher(None, name_compact, text_norm).ratio()
        if score > best_score:
            best_score = score
            best_match = name

    if best_score >= 0.5:
        return best_match
    return None


def ask_llm_for_target(user_input: str, valid_names: list[str],
                       rooms: dict[str, str]) -> str | None:
    """Chiede a Gemini di mappare l'input utente su un nome di artefatto valido.
    Se l'LLM non e' disponibile (errore/quota), usa il matching locale."""
    elenco = "\n".join(
        f"{n}  (stanza: {rooms.get(n, '')})" if rooms.get(n) else n
        for n in valid_names
    )

    system_prompt = (
        "Sei un traduttore di comandi per un agente robotico in un edificio.\n"
        "Riceverai un comando in linguaggio naturale e una lista di artefatti validi "
        "(ogni riga riporta il nome dell'artefatto e, tra parentesi, la stanza in cui "
        "si trova).\n"
        "Devi rispondere SOLO con uno dei nomi esatti di artefatto dalla lista "
        "(la parte PRIMA delle parentesi), senza nient'altro:\n"
        "niente spiegazioni, niente virgolette, niente punteggiatura, niente stanza.\n"
        "Se non riesci a determinare con sufficiente sicurezza a quale artefatto "
        "corrisponda il comando, rispondi esattamente con la parola: NONE\n\n"
        "Artefatti validi:\n" + elenco
    )

    full_prompt = system_prompt + "\n\nComando utente: " + user_input

    try:
        response = gemini_model.generate_content(full_prompt)
        text = response.text.strip()
    except Exception as e:
        print(f"[AI bridge] LLM non disponibile ({e}), uso matching locale.")
        return _local_match(user_input, valid_names)

    if text == "NONE" or text not in valid_names:
        return None
    return text


def write_target(target: str):
    with open(TARGET_JSON, "w", encoding="utf-8") as f:
        json.dump({"target": target}, f, ensure_ascii=False, indent=2)


def main():
    print("=== AI Bridge (artefatti) ===")
    print(f"Cerco il file degli artefatti in: {GRAPH_DIR}")

    try:
        valid_names, rooms = load_valid_artifacts()
    except FileNotFoundError as e:
        print(f"ERRORE: {e}")
        return

    if not valid_names:
        print("ATTENZIONE: nessun artefatto trovato in *_artifacts.json (lista vuota).")
        print("Controlla che l'esplorazione abbia scoperto e salvato almeno un oggetto.")
        return

    print(f"Artefatti validi trovati ({len(valid_names)}):")
    for n in valid_names:
        stanza = rooms.get(n, "")
        print(f"  - {n}" + (f"  (stanza: {stanza})" if stanza else ""))

    print("\nScrivi un comando (es. 'portami all'artefatto dell'ufficio5'), "
          "oppure 'exit' per uscire.")

    while True:
        user_input = input("\n> ").strip()
        if user_input.lower() in ("exit", "quit"):
            break
        if not user_input:
            continue

        target = ask_llm_for_target(user_input, valid_names, rooms)

        if target is None:
            print("Non ho capito a quale artefatto ti riferisci. Riprova.")
            continue

        write_target(target)
        print(f"-> Target impostato: {target}  (scritto in {TARGET_JSON})")


if __name__ == "__main__":
    main()
