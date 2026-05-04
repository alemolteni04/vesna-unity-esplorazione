package artifact;

import artifact.lib.maselements.AbstractMasElementArtifact;
import artifact.lib.model.WsMessage;
import artifact.lib.utils.ObjectMapperUtils;
import cartago.OPERATION;
import com.fasterxml.jackson.core.type.TypeReference;

// ============================================================
// CorridorArtifact.java
// ============================================================
// Artefatto JaCaMo per un CORRIDOIO fisico nella scena.
//
// ESISTE FIN DALL'INIZIO nel .jcm — è un dato fisico
// dell'ambiente, come una porta o una finestra.
// Non gestisce l'algoritmo di esplorazione.
//
// PERCHÉ ESISTE:
// Rappresenta il corridoio come entità fisica nel mondo.
// Contiene la posizione dei suoi due poli (estremi fisici)
// e l'ID univoco. Dopo l'esplorazione l'agente Jason sa
// che questo corridoio esiste e dove sono i suoi estremi —
// utile per la pianificazione del percorso.
//
// COSA NON FA (rispetto alla versione precedente):
//   - Non gestisce touched_pole / goto_pole
//   - Non costruisce sequenze di ispezione
//   - Non ha status algoritmici (idle/transiting/inspection)
//   - Non conosce le porte del corridoio a priori
//
// PROPRIETÀ FISICHE OSSERVABILI:
//   corridorId : String — ID univoco
//   pole1x/y/z : float  — posizione primo estremo
//   pole2x/y/z : float  — posizione secondo estremo
//
// Le coordinate dei poli vengono mandate da Unity all'avvio
// tramite "init_poles" — CorridorData.cs le invia una volta
// sola quando la scena parte, così JaCaMo le conosce.
// ============================================================
public class CorridorArtifact extends AbstractMasElementArtifact {

    @OPERATION
    void init(String id, int port) {
        super.init(id, port);

        defineObsProperty("corridorId", id);

        // Posizioni poli — inizialmente vuote, arrivano da Unity
        // tramite "init_poles" appena la scena parte
        defineObsProperty("pole1x", 0f);
        defineObsProperty("pole1y", 0f);
        defineObsProperty("pole1z", 0f);
        defineObsProperty("pole2x", 0f);
        defineObsProperty("pole2y", 0f);
        defineObsProperty("pole2z", 0f);

        // true quando Unity ha mandato le coordinate reali
        defineObsProperty("polesInitialized", false);
    }

    // --------------------------------------------------------
    // MESSAGGI DA UNITY
    // --------------------------------------------------------
    @Override
    public void onMessageReceived(String message) {
        try {
            WsMessage wsMsg = ObjectMapperUtils.convertJsonStringToObject(
                    message, new TypeReference<>() {});

            // CorridorData.cs manda "init_poles" una volta all'avvio
            // con le coordinate reali dei due poli dalla scena Unity.
            // Payload: { pole1: {x,y,z}, pole2: {x,y,z} }
            if ("init_poles".equals(wsMsg.getOperation())) {
                java.util.Map<String, Object> pole1 =
                    (java.util.Map<String, Object>) wsMsg.getParam("pole1");
                java.util.Map<String, Object> pole2 =
                    (java.util.Map<String, Object>) wsMsg.getParam("pole2");

                if (pole1 != null) {
                    getObsProperty("pole1x").updateValue(
                        ((Number) pole1.get("x")).floatValue());
                    getObsProperty("pole1y").updateValue(
                        ((Number) pole1.get("y")).floatValue());
                    getObsProperty("pole1z").updateValue(
                        ((Number) pole1.get("z")).floatValue());
                }
                if (pole2 != null) {
                    getObsProperty("pole2x").updateValue(
                        ((Number) pole2.get("x")).floatValue());
                    getObsProperty("pole2y").updateValue(
                        ((Number) pole2.get("y")).floatValue());
                    getObsProperty("pole2z").updateValue(
                        ((Number) pole2.get("z")).floatValue());
                }

                getObsProperty("polesInitialized").updateValue(true);
                writeLog("[" + artifactName + "] poli inizializzati: "
                         + "P1=" + pole1 + " P2=" + pole2);
            }

            execInternalOp("signalAgentsByTick");

        } catch (Exception e) {
            logger.info("CorridorArtifact error: " + e);
        }
    }
}