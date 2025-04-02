using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.VisualScripting;

[Inspectable]
public struct Bullet
{
    [Inspectable]
    public Vector2 position; // the position of the bullet on the board

    [Inspectable]
    public int direction; // up = 0, left = 1, down = 2, right = 3
}

public class BulletSpawner : Unit
{
    enum BoardState
    {
        Empty = 0,
        Player = 1,
        BulletUp = 1 << 1,
        BulletLeft = 1 << 2,
        BulletDown = 1 << 3,
        BulletRight = 1 << 4,
        Bullet = BulletUp | BulletLeft | BulletDown | BulletRight,
    }

    class BoardNode
    {
        // Connections to nodes on the previous turn
        public List<BoardNode> prev = new List<BoardNode>();

        // Connections to nodes on the next turn
        public List<BoardNode> next = new List<BoardNode>();
    }

    [DoNotSerialize]
    public ControlInput inputTrigger;

    [DoNotSerialize]
    public ControlOutput outputTrigger;

    [DoNotSerialize]
    public ValueInput playerPosition;

    [DoNotSerialize]
    public ValueInput bullets;

    [DoNotSerialize]
    public ValueInput boardSize;

    [DoNotSerialize]
    public ValueInput numBulletsToSpawn;

    [DoNotSerialize]
    public ValueOutput bulletsToSpawn;

    private List<Bullet> outputBullets;

    private static Vector2[] directions = new Vector2[4]
    {
        new Vector2(0, -1), // up
        new Vector2(-1, 0), // left
        new Vector2(0, 1), // down
        new Vector2(1, 0), // right
    };

    protected override void Definition()
    {
        inputTrigger = ControlInput(
            "SpawnBullets",
            (flow) =>
            {
                Vector2 playerPos = flow.GetValue<Vector2>(playerPosition);
                List<Bullet> bulletData = flow.GetValue<List<Bullet>>(bullets);
                int size = flow.GetValue<int>(boardSize);
                int numBullets = flow.GetValue<int>(numBulletsToSpawn);
                outputBullets = GetBulletSpawns(
                    GameDataToBoard(playerPos, bulletData, size),
                    numBullets
                );
                return outputTrigger;
            }
        );
        outputTrigger = ControlOutput("Output");

        playerPosition = ValueInput<Vector2>("playerPosition", Vector2.zero);
        bullets = ValueInput<List<Bullet>>("bullets", new List<Bullet>());
        boardSize = ValueInput<int>("boardSize", 7);
        numBulletsToSpawn = ValueInput<int>("numBulletsToSpawn", 1);
        bulletsToSpawn = ValueOutput<List<Bullet>>("bulletsToSpawn", (flow) => outputBullets);

        Succession(inputTrigger, outputTrigger);
        Assignment(inputTrigger, bulletsToSpawn);
    }

    private int[,] GameDataToBoard(Vector2 playerPos, List<Bullet> bulletData, int size)
    {
        int[,] board = new int[size, size];
        // Set the player position
        Vector2 playerIdx = PositionToIndex(playerPos, size);
        board[(int)playerIdx.y, (int)playerIdx.x] |= (int)BoardState.Player;
        // Set the bullet positions on the board
        foreach (Bullet bullet in bulletData)
        {
            Vector2 posIdx = PositionToIndex(bullet.position, size);
            int row = (int)posIdx.y;
            int col = (int)posIdx.x;
            if (row >= 0 && row < size && col >= 0 && col < size)
            {
                board[row, col] |= (1 << (bullet.direction + 1));
            }
        }
        return board;
    }

    // This function will attempt to find a set of bullet spawns
    // for the specific board state up to the number of bullets specified.
    // If there are not enough valid spawns, it will return as many as possible.
    private List<Bullet> GetBulletSpawns(int[,] board, int numBulletsToSpawn)
    {
        // First, create a directed graph where each node is a tile and each layer is a turn.
        // A connection between two nodes is a valid move for the player that will not hit a bullet.
        // The array is indexed by (turn, row, col) or (turn, y, x)
        int boardSize = board.GetLength(0);
        // After boardSize - 2 turns, all bullets spawned on turn 0 will be gone. Add 1 for the current turn.
        int turnsToSim = boardSize - 1;
        BoardNode[,,] boardGraph = new BoardNode[turnsToSim, boardSize, boardSize];
        for (int turn = 0; turn < turnsToSim; turn++)
        {
            for (int row = 0; row < boardSize; row++)
            {
                for (int col = 0; col < boardSize; col++)
                {
                    boardGraph[turn, row, col] = new BoardNode();
                }
            }
        }

        // Find the player
        BoardNode playerNode = null;
        for (int row = 0; row < boardSize; row++)
        {
            for (int col = 0; col < boardSize; col++)
            {
                if ((board[row, col] & (int)BoardState.Player) > 0)
                {
                    playerNode = boardGraph[0, row, col];
                    playerNode.prev.Add(playerNode);
                    Debug.Log("Player found at (" + col + ", " + row + ")");
                    break;
                }
            }
        }
        if (playerNode == null)
        {
            throw new Exception("Player not found on board");
        }

        // Create the board states for each turn
        int[][,] boardStates = new int[turnsToSim][,];
        boardStates[0] = board;
        for (int turn = 0; turn < turnsToSim - 1; turn++)
        {
            boardStates[turn + 1] = AdvanceBoard(boardStates[turn]);
        }

        // For each turn, the nodes that have connections to the previous turn
        // are the nodes that the player can get to. For each of those nodes,
        // check the 4 adjacent tiles on the next turn to see if they are valid moves.
        for (int turn = 0; turn < turnsToSim - 1; turn++)
        {
            for (int row = 1; row < boardSize - 1; row++)
            {
                for (int col = 1; col < boardSize - 1; col++)
                {
                    BoardNode currentNode = boardGraph[turn, row, col];
                    if (currentNode.prev.Count == 0)
                        continue; // No connections to the previous turn, skip this node

                    // Check the 4 adjacent tiles
                    // 0 = up, 1 = left, 2 = down, 3 = right
                    for (int d = 0; d < 4; d++)
                    {
                        Vector2 playerDir = BulletSpawner.directions[d];
                        int nextRow = row + (int)playerDir.y;
                        int nextCol = col + (int)playerDir.x;

                        // Player can only move in the inner tiles
                        bool validIdx =
                            nextRow >= 1
                            && nextRow < boardSize - 1
                            && nextCol >= 1
                            && nextCol < boardSize - 1;
                        if (!validIdx)
                            continue; // Skip invalid indices

                        // Check if the next tile has a bullet
                        bool hasBullet =
                            (boardStates[turn + 1][nextRow, nextCol] & (int)BoardState.Bullet) > 0;
                        if (hasBullet)
                            continue; // Player is moving into a tile with a bullet on it

                        // Check if player is swapping with a bullet
                        int bullet = boardStates[turn][nextRow, nextCol] & (int)BoardState.Bullet;
                        // 0 = up, 1 = left, 2 = down, 3 = right
                        // 1 = BulletUp, 2 = BulletLeft, 3 = BulletDown, 4 = BulletRight
                        int bitShift;
                        if (d == 0) // up
                            bitShift = 3; // BulletDown
                        else if (d == 1) // left
                            bitShift = 4; // BulletRight
                        else if (d == 2) // down
                            bitShift = 1; // BulletUp
                        else // right
                            bitShift = 2; // BulletLeft
                        if ((bullet & (1 << bitShift)) > 0)
                            continue; // Player is swapping with a bullet, skip this node

                        BoardNode nextNode = boardGraph[turn + 1, nextRow, nextCol];
                        currentNode.next.Add(nextNode);
                        nextNode.prev.Add(currentNode);
                    }
                }
            }
        }

        // Check if there are any connections to the last turn
        bool isPossible = false;
        for (int row = 0; row < boardSize; row++)
        {
            for (int col = 0; col < boardSize; col++)
            {
                BoardNode currentNode = boardGraph[turnsToSim - 1, row, col];
                if (currentNode.prev.Count > 0)
                {
                    isPossible = true;
                    break;
                }
            }
        }
        if (!isPossible)
        {
            Debug.Log("Player will be stuck, no bullets can be spawned");
            return new List<Bullet>();
        }

        // Now we check each possible bullet spawn location and see if it is valid.
        // A valid spawn location is one that will not completely block player paths, that is,
        // the last layer of the graph must have at least one connection to the previous layer.
        // Bullets can only spawn on the extreme edges of the board and direct into the board
        List<Vector2> spawnLocations = new List<Vector2>();
        for (int row = 0; row < boardSize; row++)
        {
            for (int col = 0; col < boardSize; col++)
            {
                // Check if the tile is on the edge of the board and not a corner tile
                bool isEdgeTile =
                    row == 0 || row == boardSize - 1 || col == 0 || col == boardSize - 1;
                bool isCornerTile =
                    (row == 0 && col == 0)
                    || (row == 0 && col == boardSize - 1)
                    || (row == boardSize - 1 && col == 0)
                    || (row == boardSize - 1 && col == boardSize - 1);
                if (!isEdgeTile || isCornerTile)
                    continue; // Skip non-edge tiles and corner tiles

                spawnLocations.Add(new Vector2(col, row));
            }
        }

        // TODO: Store the changed board graphs for each spawn set OR recompute the board graph every time
        // List of list of indices of valid spawn locations
        // Each list of indices represents a set of bullets that can be spawned
        List<List<int>> validSpawnSets = new List<List<int>>();
        for (int i = 0; i < spawnLocations.Count; i++)
        {
            // Check if the spawn location is valid
            if (IsValidSpawn(boardGraph, spawnLocations[i]))
            {
                // If it is valid, add it to the list of valid spawn locations
                List<int> spawnSet = new List<int>();
                spawnSet.Add(i);
                validSpawnSets.Add(spawnSet);
            }
        }

        // Add more bullets until you can't add any more
        while (validSpawnSets[0].Count < numBulletsToSpawn)
        {
            List<List<int>> newValidSpawnSets = new List<List<int>>();
            for (int i = 0; i < validSpawnSets.Count; i++)
            {
                List<int> spawnSet = validSpawnSets[i];
                // look forward from the index of the last bullet in the spawn set
                // to avoid duplicates
                for (int j = spawnSet[spawnSet.Count - 1] + 1; j < spawnLocations.Count; j++)
                {
                    // Check if the spawn location is valid
                    if (IsValidSpawn(boardGraph, spawnLocations[j]))
                    {
                        // If it is valid, add it to the list of valid spawn locations
                        List<int> newSpawnSet = new List<int>(spawnSet);
                        newSpawnSet.Add(j);
                        newValidSpawnSets.Add(newSpawnSet);
                    }
                }
            }
            if (newValidSpawnSets.Count == 0)
            {
                break; // No more valid spawn sets, break out of the loop
            }
            validSpawnSets = newValidSpawnSets; // Update the valid spawn sets
        }
        // check that all the spawn sets have the same number of bullets
        for (int i = 0; i < validSpawnSets.Count; i++)
        {
            if (validSpawnSets[i].Count != validSpawnSets[0].Count)
            {
                throw new Exception("Spawn sets have different number of bullets");
            }
        }
        Debug.Log("Found " + validSpawnSets.Count + " valid spawn sets");

        // Convert the valid spawn sets to bullet objects
        List<Bullet> bullets = new List<Bullet>();
        for (int i = 0; i < validSpawnSets[0].Count; i++)
        {
            int spawnIdx = validSpawnSets[0][i];
            Vector2 spawnPos = spawnLocations[spawnIdx];
            Vector2 posIdx = IndexToPosition(spawnPos, boardSize);
            int direction = 0;
            // up = 0, left = 1, down = 2, right = 3
            if (spawnPos.y == boardSize - 1)
            {
                direction = 0; // up
            }
            else if (spawnPos.x == boardSize - 1)
            {
                direction = 1; // left
            }
            else if (spawnPos.y == 0)
            {
                direction = 2; // down
            }
            else if (spawnPos.x == 0)
            {
                direction = 3; // right
            }
            Bullet bullet = new Bullet { position = posIdx, direction = direction, };
            bullets.Add(bullet);
        }
        return bullets;
    }

    // Converts a 2D array index to a 2D position on the board
    // (0,0) is the top left of the array,
    // (0,0) is the center of the board.
    /* Example: size = 3
    *   (0,0) (1,0) (2,0)    (-1,1)  (0,1)  (1,1)
    *   (0,1) (1,1) (2,1) -> (-1,0)  (0,0)  (1,0)
    *   (0,2) (1,2) (2,2)    (-1,-1) (0,-1) (1,-1)
    */
    private Vector2 IndexToPosition(Vector2 idx, int size)
    {
        int center = size / 2;
        return new Vector2(idx.x - center, center - idx.y);
    }

    /* Example: size = 3
    *   (-1,1)  (0,1)  (1,1)     (0,0) (1,0) (2,0)
    *   (-1,0)  (0,0)  (1,0)  -> (0,1) (1,1) (2,1)
    *   (-1,-1) (0,-1) (1,-1)    (0,2) (1,2) (2,2)
    */
    private Vector2 PositionToIndex(Vector2 pos, int size)
    {
        int center = size / 2;
        return new Vector2(pos.x + center, center - pos.y);
    }

    // Advances the board by one turn, moving the bullets
    private int[,] AdvanceBoard(int[,] board)
    {
        int boardSize = board.GetLength(0);
        int[,] nextBoard = new int[boardSize, boardSize];
        for (int row = 0; row < boardSize; row++)
        {
            for (int col = 0; col < boardSize; col++)
            {
                int bullet = board[row, col] & (int)BoardState.Bullet;
                if (bullet == 0)
                    continue;

                // Move the bullet in the direction it is facing
                // One tile can possibly have multiple bullets
                // 0 = up, 1 = left, 2 = down, 3 = right
                for (int d = 0; d < 4; d++)
                {
                    // 1 << 1 = BulletUp, 1 << 2 = BulletLeft, 1 << 3 = BulletDown, 1 << 4 = BulletRight
                    int dir = bullet & (1 << (d + 1));
                    if (dir == 0)
                        continue; // No bullet in this direction

                    Vector2 direction = BulletSpawner.directions[d];
                    int nextRow = row + (int)direction.y;
                    int nextCol = col + (int)direction.x;
                    bool validIdx =
                        nextRow >= 0 && nextRow < boardSize && nextCol >= 0 && nextCol < boardSize;
                    if (validIdx)
                    {
                        nextBoard[nextRow, nextCol] |= dir;
                    }
                }
            }
        }
        return nextBoard;
    }

    // Checks if spawning a bullet at the given location will completely cut off
    // all paths for the player. This is done by removing node connections at each
    // turn the bullet will be on.
    private bool IsValidSpawn(BoardNode[,,] boardGraph, Vector2 posIdx)
    {
        // determine the direction of the bullet
        Vector2 direction = new Vector2(0, 0);
        int size = boardGraph.GetLength(1);
        if (posIdx.x == 0)
            direction = new Vector2(1, 0); // right
        else if (posIdx.x == size - 1)
            direction = new Vector2(-1, 0); // left
        else if (posIdx.y == 0)
            direction = new Vector2(0, 1); // down
        else if (posIdx.y == size - 1)
            direction = new Vector2(0, -1); // up

        Queue<BoardNode> queue = new Queue<BoardNode>();

        // Find all the nodes that the bullet will be on
        // and remove all connections to the previous turn
        for (int turn = 0; turn < boardGraph.GetLength(0); turn++)
        {
            Vector2 currentPos = posIdx + direction * turn;
            int row = (int)currentPos.y;
            int col = (int)currentPos.x;
            BoardNode currentNode = boardGraph[turn, row, col];
            if (currentNode.prev.Count == 0)
                continue; // No connections to the previous turn, skip this node

            queue.Enqueue(currentNode);
            // remove all connections to the previous turn
            foreach (BoardNode prevNode in currentNode.prev)
            {
                prevNode.next.Remove(currentNode);
            }
            currentNode.prev.Clear();
        }

        // Remove all forward connections from the nodes the bullet will be on
        while (queue.Count > 0)
        {
            BoardNode currentNode = queue.Dequeue();
            foreach (BoardNode nextNode in currentNode.next)
            {
                nextNode.prev.Remove(currentNode);
                if (nextNode.prev.Count == 0)
                {
                    queue.Enqueue(nextNode); // add to queue if it has no more connections
                }
            }
            currentNode.next.Clear();
        }

        // Check if there are any connections to the last turn
        int lastTurn = boardGraph.GetLength(0) - 1;
        for (int row = 0; row < boardGraph.GetLength(1); row++)
        {
            for (int col = 0; col < boardGraph.GetLength(2); col++)
            {
                BoardNode currentNode = boardGraph[lastTurn, row, col];
                if (currentNode.prev.Count > 0)
                {
                    return true; // valid spawn
                }
            }
        }
        return false; // invalid spawn, all paths are blocked
    }
}
