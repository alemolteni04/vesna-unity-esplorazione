using System.Collections.Generic;
using UnityEngine;

// ============================================================
// GraphSnapshotBuilder.cs
// ============================================================
// Converte il grafo vivo (TopologicalGraph) in un BuildingSnapshot
// serializzabile e persistibile su disco.
//
// RESPONSABILITÀ UNICA:
//   Tradurre i tipi runtime (GraphNode, GraphEdge, Vector3, enum)
//   nei corrispondenti tipi DTO di BuildingSnapshot.
//   Non sa nulla di WebSocket, JaCaMo, o filesystem.
//
// COME ESTENDERE:
//   • Nuovo campo in GraphNode/GraphEdge → aggiungi il mapping
//     in NodeFrom() / EdgeFrom() e il campo in NodeSnapshot/EdgeSnapshot.
//   • Nuovo tipo di nodo → aggiungi un branch in NodeFrom() se
//     ha campi specifici (es. corridorId per i poli), altrimenti
//     nessuna modifica necessaria.
//   • Nuovo tipo di link cross-floor → aggiungi un ConnectorFrom()
//     analogo e chiamalo in Build().
// ============================================================

public static class GraphSnapshotBuilder
{
    // --------------------------------------------------------
    // ENTRY POINT
    // --------------------------------------------------------

    /// <summary>
    /// Costruisce uno snapshot completo dell'edificio a partire dai
    /// dati runtime di ExplorationManager.
    /// </summary>
    /// <param name="floorGraphs">Dizionario piano → grafo topologico.</param>
    /// <param name="connectorLinks">Link cross-floor (scale, ascensori).</param>
    /// <param name="roomDistances">Coppie di distanza NavMesh tra porte.</param>
    /// <param name="buildingId">Nome/ID logico dell'edificio (es. "Edificio_A").</param>
    public static BuildingSnapshot Build(
        Dictionary<int, TopologicalGraph> floorGraphs,
        List<FloorConnectorLink>          connectorLinks,
        List<RoomDistancePair>            roomDistances,
        string                            buildingId = "building")
    {
        var snapshot = new BuildingSnapshot
        {
            buildingId   = buildingId,
            capturedAt   = System.DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
        };

        // ── Piani ────────────────────────────────────────────
        var sortedFloors = new List<int>(floorGraphs.Keys);
        sortedFloors.Sort();

        foreach (int floorIndex in sortedFloors)
        {
            var floor    = new FloorSnapshot { floorIndex = floorIndex };
            var graph    = floorGraphs[floorIndex];

            foreach (var node in graph.AllNodes())
            {
                if (node == null) continue;
                floor.nodes.Add(NodeFrom(node));

                foreach (var edge in node.edges)
                {
                    if (edge == null) continue;
                    // Gli archi sono salvati con il floorIndex per ricollegarli
                    // al piano corretto al momento del caricamento.
                    floor.edges.Add(EdgeFrom(edge));
                }
            }

            snapshot.floors.Add(floor);
        }

        // ── Connettori cross-floor ───────────────────────────
        foreach (var link in connectorLinks)
            snapshot.connectorLinks.Add(ConnectorLinkFrom(link));

        // ── Distanze porte ───────────────────────────────────
        foreach (var pair in roomDistances)
            snapshot.roomDistances.Add(RoomDistFrom(pair));

        
        Debug.Log($"[SnapshotBuilder] Snapshot creato: " +
                  $"{snapshot.floors.Count} piani, " +
                  $"{snapshot.connectorLinks.Count} link, " +
                  $"{snapshot.roomDistances.Count} distanze porte.");

       
        return snapshot;

    }

    // --------------------------------------------------------
    // CONVERSORI PRIVATI
    // Ogni metodo converte un solo tipo runtime → DTO.
    // Facili da trovare, facili da modificare indipendentemente.
    // --------------------------------------------------------

    private static NodeSnapshot NodeFrom(GraphNode node)
    {
        var s = new NodeSnapshot
        {
            nodeId           = node.id,
            nodeType         = node.type.ToString(),
            x                = node.position.x,
            y                = node.position.y,
            z                = node.position.z,
            fullyExplored    = node.fullyExplored,
            physicallyVisited = node.physicallyVisited,
        };

        // Campi specifici dei nodi polo corridoio
        if (node is CorridorPoleNode pole)
        {
            s.corridorId = pole.corridorId;
            s.poleLabel  = pole.poleLabel;
        }

        return s;
    }

   private static EdgeSnapshot EdgeFrom(GraphEdge edge)
{
    var s = new EdgeSnapshot
    {
        edgeId         = edge.id,
        fromNodeId     = edge.fromNodeId,
        toNodeId       = edge.toNodeId ?? "unknown",
        edgeType       = edge.edgeType.ToString(),
        edgeState      = edge.state.ToString(),
        doorState      = edge.doorState.ToString(),
        isPhysical     = edge.isPhysical,
        isCorridorLink = edge.isCorridorLink,
        x              = edge.position.x,
        y              = edge.position.y,
        z              = edge.position.z,
    };

    // Campi solo per archi corridoio
    if (edge.edgeType == EdgeType.DoorFW ||
        edge.edgeType == EdgeType.DoorBW)
    {
        s.side             = edge.side;
        s.distFromA        = edge.distFromA;
        s.distFromB        = edge.distFromB;
        s.orderFW          = edge.orderFW;
        s.orderBW          = edge.orderBW;
        s.explorationOrder = edge.explorationOrder;
    }

    if (edge.edgeType == EdgeType.Central)
    {
        s.distance = edge.distFromA; // distFromA contiene la lunghezza totale del corridoio
    }

    return s;
}
   private static ConnectorLinkSnapshot ConnectorLinkFrom(FloorConnectorLink link)
    {
        return new ConnectorLinkSnapshot
        {
            connectorId   = link.connectorId,
            floorA        = link.floorFrom,
            floorB        = link.floorTo,
            bidirectional = link.bidirectional,
            posAx         = link.posFrom.x,
            posAy         = link.posFrom.y,
            posAz         = link.posFrom.z,
            posBx         = link.posTo.x,
            posBy         = link.posTo.y,
            posBz         = link.posTo.z,
        };
    }

    private static RoomDistSnapshot RoomDistFrom(RoomDistancePair pair)
    {
        return new RoomDistSnapshot
        {
            roomA    = pair.roomA,
            roomB    = pair.roomB,
            floor    = pair.floor,
            distance = pair.distance,
        };
    }
}
