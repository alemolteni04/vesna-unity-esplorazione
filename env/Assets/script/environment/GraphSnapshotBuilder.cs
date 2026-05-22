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
    /// <param name="doorDistances">Coppie di distanza NavMesh tra porte.</param>
    /// <param name="buildingId">Nome/ID logico dell'edificio (es. "Edificio_A").</param>
    public static BuildingSnapshot Build(
        Dictionary<int, TopologicalGraph> floorGraphs,
        List<FloorConnectorLink>          connectorLinks,
        List<DoorDistancePair>            doorDistances,
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
        foreach (var pair in doorDistances)
            snapshot.doorDistances.Add(DoorDistFrom(pair));

        Debug.Log($"[SnapshotBuilder] Snapshot creato: " +
                  $"{snapshot.floors.Count} piani, " +
                  $"{snapshot.connectorLinks.Count} link, " +
                  $"{snapshot.doorDistances.Count} distanze porte.");

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
        return new EdgeSnapshot
        {
            edgeId           = edge.id,
            fromNodeId       = edge.fromNodeId,
            toNodeId         = edge.toNodeId ?? "unknown",
            edgeType         = edge.edgeType.ToString(),
            edgeState        = edge.state.ToString(),
            doorState        = edge.doorState.ToString(),
            isPhysical       = edge.isPhysical,
            isCorridorLink   = edge.isCorridorLink,
            side             = edge.side,
            distFromA        = edge.distFromA,
            distFromB        = edge.distFromB,
            orderFW          = edge.orderFW,
            orderBW          = edge.orderBW,
            explorationOrder = edge.explorationOrder,
            navMeshDist      = edge.navMeshDist,
            x                = edge.position.x,
            y                = edge.position.y,
            z                = edge.position.z,
        };
    }

    private static ConnectorLinkSnapshot ConnectorLinkFrom(FloorConnectorLink link)
    {
        return new ConnectorLinkSnapshot
        {
            connectorId = link.connectorId,
            floorFrom   = link.floorFrom,
            nodeIdFrom  = link.nodeIdFrom,
            posFromX    = link.posFrom.x,
            posFromY    = link.posFrom.y,
            posFromZ    = link.posFrom.z,
            floorTo     = link.floorTo,
            nodeIdTo    = link.nodeIdTo,
            posToX      = link.posTo.x,
            posToY      = link.posTo.y,
            posToZ      = link.posTo.z,
        };
    }

    private static DoorDistSnapshot DoorDistFrom(DoorDistancePair pair)
    {
        return new DoorDistSnapshot
        {
            doorA    = pair.doorA,
            doorB    = pair.doorB,
            floor    = pair.floor,
            distance = pair.distance,
        };
    }
}
