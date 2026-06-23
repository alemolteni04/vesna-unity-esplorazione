package vesna;

import jason.asSemantics.*;
import jason.asSyntax.*;

import java.io.*;
import java.nio.file.*;

/**
 * Internal action: checkTarget(Target)
 *
 * Legge target.json (scritto da ai_bridge.py) e, se contiene un nuovo
 * target rispetto all'ultima volta, lo unifica con la variabile Target
 * e ritorna true. Se non c'è nessun nuovo target, fallisce (false).
 *
 * target.json formato: {"target": "Laboratorio3"}
 */
public class CheckTarget extends DefaultInternalAction {

    // Path del file scritto da ai_bridge.py (stessa cartella di building.json)
    private static final String TARGET_FILE =
            System.getProperty("user.home")
            + "\\AppData\\LocalLow\\DefaultCompany\\JaCaMoIntegration\\graph_snapshots\\target.json";

    // Ricorda l'ultimo target già processato, per non ri-triggerare lo stesso goal
    private String lastTarget = null;

    @Override
    public Object execute(TransitionSystem ts, Unifier un, Term[] args) throws Exception {

        File f = new File(TARGET_FILE);
        if (!f.exists()) {
            return false; // nessun comando ancora arrivato
        }

        String content;
        try {
            content = new String(Files.readAllBytes(f.toPath()), "UTF-8");
        } catch (IOException e) {
            return false;
        }

        // Parsing JSON minimale: estrae il valore di "target"
        String target = extractTargetField(content);
        if (target == null || target.isEmpty()) {
            return false;
        }

        if (target.equals(lastTarget)) {
            return false; // stesso comando di prima, niente di nuovo
        }

        lastTarget = target;

        // args[0] = Target (variabile da unificare nel piano Jason)
        //Term targetTerm = new Atom(target);
        Term targetTerm = ASSyntax.createString(target);
        return un.unifies(args[0], targetTerm);
    }

    /**
     * Estrae il valore della chiave "target" da un JSON semplice del tipo
     * {"target": "NomeStanza"}.
     */
    private String extractTargetField(String json) {
        int keyIdx = json.indexOf("\"target\"");
        if (keyIdx < 0) return null;

        int colonIdx = json.indexOf(':', keyIdx);
        if (colonIdx < 0) return null;

        int firstQuote = json.indexOf('"', colonIdx + 1);
        if (firstQuote < 0) return null;

        int secondQuote = json.indexOf('"', firstQuote + 1);
        if (secondQuote < 0) return null;

        return json.substring(firstQuote + 1, secondQuote);
    }
}
