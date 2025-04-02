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

    public Bullet(Vector2 position, int direction)
    {
        this.position = position;
        this.direction = direction;
    }
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
        // The turn of the node on the board
        public int turn;

        // The row and column of the node on the board
        public int row;
        public int col;

        // Connections to nodes on the previous turn
        public List<BoardNode> prev = new List<BoardNode>();

        // Connections to nodes on the next turn
        public List<BoardNode> next = new List<BoardNode>();

        public BoardNode(int turn, int row, int col)
        {
            this.turn = turn;
            this.row = row;
            this.col = col;
        }
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

    private int[,] GameDataToBoard(in Vector2 playerPos, in List<Bullet> bulletData, in int size)
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

    /// <summary>
    /// This function will attempt to find a set of bullet spawns
    /// for the specific board state up to the number of bullets specified.
    /// If there are not enough valid spawns, it will return as many as possible.
    /// Throws an exception if the player is not found on the board or if
    /// the spawn sets have different number of bullets. This should never happen, but it is here for safety.
    /// </summary>
    /// <param name="board"></param>
    /// <param name="numBulletsToSpawn"></param>
    /// <returns>Returns a list of valid bullets to spawn of max size numBulletsToSpawn</returns>
    /// <exception cref="Exception"></exception>
    private List<Bullet> GetBulletSpawns(in int[,] board, in int numBulletsToSpawn)
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
                    boardGraph[turn, row, col] = new BoardNode(turn, row, col);
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
        Queue<BoardNode> queue = new Queue<BoardNode>();
        queue.Enqueue(playerNode);
        while (queue.Count > 0)
        {
            BoardNode currentNode = queue.Dequeue();
            if (currentNode.turn == turnsToSim - 1)
                continue; // Skip the last turn, we don't need to check it

            // Check the 4 adjacent tiles
            // 0 = up, 1 = left, 2 = down, 3 = right
            for (int d = 0; d < 4; d++)
            {
                Vector2 playerDir = BulletSpawner.directions[d];
                int turn = currentNode.turn;
                int nextRow = currentNode.row + (int)playerDir.y;
                int nextCol = currentNode.col + (int)playerDir.x;

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
                    continue; // Player cannot move to a tile with a bullet

                // Check if player is swapping with a bullet
                // 0 = up, 1 = left, 2 = down, 3 = right
                // 1 = BulletUp, 2 = BulletLeft, 3 = BulletDown, 4 = BulletRight
                int bitShift = (d + 2) % 4 + 1;
                if ((boardStates[turn][nextRow, nextCol] & (1 << bitShift)) > 0)
                    continue; // Player cannot move into a bullet

                // The player can move into this tile, add it to the graph
                BoardNode nextNode = boardGraph[turn + 1, nextRow, nextCol];
                currentNode.next.Add(nextNode);
                nextNode.prev.Add(currentNode);
                queue.Enqueue(nextNode); // Add the next node to the queue
            }
        }

        // Check if there are any connections to the last turn
        if (!isBoardSolvable(boardGraph))
        {
            Debug.Log("Player is stuck, no bullets can be spawned");
            return new List<Bullet>();
        }

        // Now we check each possible bullet spawn location and see if it is valid.
        // A valid spawn location is one that will not completely block player paths, that is,
        // the last layer of the graph must have at least one connection to the previous layer.
        // Bullets can only spawn on the extreme edges of the board and direct into the board.
        List<Vector2> spawnLocations = GetSpawnLocations(boardSize);

        // Each tuple represents a set of bullets that can be spawned and the board graph after spawning them
        var validSpawnSets = new List<(List<int> Set, BoardNode[,,] Graph)>();
        for (int i = 0; i < spawnLocations.Count; i++)
        {
            // Create a deep copy of the board graph for each spawn set
            BoardNode[,,] boardGraphCopy = DeepCopyBoardGraph(boardGraph);
            // Check if the spawn location is valid
            if (IsValidSpawn(ref boardGraphCopy, spawnLocations[i]))
            {
                // If it is valid, add it to the list of valid spawn locations
                validSpawnSets.Add((new List<int> { i }, boardGraphCopy));
            }
        }

        // Add more bullets until you can't add any more
        while (validSpawnSets[0].Set.Count < numBulletsToSpawn)
        {
            var newValidSpawnSets = new List<(List<int> Set, BoardNode[,,] Graph)>();
            for (int i = 0; i < validSpawnSets.Count; i++)
            {
                List<int> spawnSet = validSpawnSets[i].Set;
                // look forward from the index of the last bullet in the spawn set
                // to avoid duplicates
                for (int j = spawnSet[spawnSet.Count - 1] + 1; j < spawnLocations.Count; j++)
                {
                    // Create a deep copy of the board graph for each spawn set
                    BoardNode[,,] boardGraphCopy = DeepCopyBoardGraph(validSpawnSets[i].Graph);
                    // Check if the spawn location is valid
                    if (IsValidSpawn(ref boardGraphCopy, spawnLocations[j]))
                    {
                        // If it is valid, add it to the list of valid spawn locations
                        List<int> newSpawnSet = new List<int>(spawnSet);
                        newSpawnSet.Add(j);
                        newValidSpawnSets.Add((newSpawnSet, boardGraphCopy));
                    }
                }
            }
            if (newValidSpawnSets.Count == 0)
            {
                break; // Could not find any more valid spawns
            }
            validSpawnSets = newValidSpawnSets; // Update the valid spawn sets
        }
        // check that all the spawn sets have the same number of bullets
        for (int i = 0; i < validSpawnSets.Count; i++)
        {
            if (validSpawnSets[i].Set.Count != validSpawnSets[0].Set.Count)
            {
                throw new Exception("Spawn sets have different number of bullets");
            }
        }
        Debug.Log("Found " + validSpawnSets.Count + " valid spawn sets");

        // Convert the valid spawn sets to bullet objects
        List<Bullet> bullets = new List<Bullet>();
        foreach (int spawnIdx in validSpawnSets[0].Set)
        {
            Vector2 spawnPos = spawnLocations[spawnIdx];

            // up = 0, left = 1, down = 2, right = 3
            int direction = 0;
            if (spawnPos.y == boardSize - 1)
                direction = 0; // up
            else if (spawnPos.x == boardSize - 1)
                direction = 1; // left
            else if (spawnPos.y == 0)
                direction = 2; // down
            else if (spawnPos.x == 0)
                direction = 3; // right

            bullets.Add(new Bullet(IndexToPosition(spawnPos, boardSize), direction));
        }
        return bullets;
    }

    /// <summary>
    /// Advances the board by one turn, moving the bullets in the direction they are facing.
    /// </summary>
    /// <param name="board"></param>
    /// <returns>Returns the given board advanced by one turn</returns>
    private int[,] AdvanceBoard(in int[,] board)
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
                        continue; // No bullet going this direction

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

    /// <summary>
    /// Checks if the given board is solvable, meaning the player can reach the last turn.
    /// A solvable board is one that has at least one connection to the last turn.
    /// </summary>
    /// <param name="boardGraph"></param>
    /// <returns>Returns true if the board is valid, otherwise returns false</returns>
    private bool isBoardSolvable(in BoardNode[,,] boardGraph)
    {
        int lastTurn = boardGraph.GetLength(0) - 1;
        int size = boardGraph.GetLength(1);
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                BoardNode currentNode = boardGraph[lastTurn, row, col];
                if (currentNode.prev.Count > 0)
                {
                    return true; // The last turn can be reached
                }
            }
        }
        return false; // Invalid board, no way to get to the last turn
    }

    /// <summary>
    /// Gets the possible spawn locations for bullets for a board of the given size.
    /// Bullets can only spawn on the extreme edges of the board.
    /// </summary>
    /// <param name="size"></param>
    /// <returns>Returns a list of the possible spawn locations</returns>
    private List<Vector2> GetSpawnLocations(in int size)
    {
        List<Vector2> spawnLocations = new List<Vector2>();
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                // Check if the tile is on the edge of the board and not a corner tile
                bool isEdgeTile = row == 0 || row == size - 1 || col == 0 || col == size - 1;
                bool isCornerTile =
                    (row == 0 && col == 0)
                    || (row == 0 && col == size - 1)
                    || (row == size - 1 && col == 0)
                    || (row == size - 1 && col == size - 1);
                if (!isEdgeTile || isCornerTile)
                    continue; // Skip non-edge tiles and corner tiles
                spawnLocations.Add(new Vector2(col, row));
            }
        }
        return spawnLocations;
    }

    /// <summary>
    /// Creates a deep copy of the board graph.
    /// </summary>
    /// <param name="boardGraph"></param>
    /// <returns></returns>
    private BoardNode[,,] DeepCopyBoardGraph(in BoardNode[,,] boardGraph)
    {
        int turns = boardGraph.GetLength(0);
        int size = boardGraph.GetLength(1);
        BoardNode[,,] newBoardGraph = new BoardNode[turns, size, size];
        for (int turn = 0; turn < turns; turn++)
        {
            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    newBoardGraph[turn, row, col] = new BoardNode(turn, row, col);
                }
            }
        }
        // Copy the connections
        for (int turn = 0; turn < turns; turn++)
        {
            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    BoardNode currentNode = boardGraph[turn, row, col];
                    BoardNode newNode = newBoardGraph[turn, row, col];
                    foreach (BoardNode nextNode in currentNode.next)
                    {
                        newNode.next.Add(newBoardGraph[turn + 1, nextNode.row, nextNode.col]);
                    }
                    foreach (BoardNode prevNode in currentNode.prev)
                    {
                        newNode.prev.Add(newBoardGraph[turn - 1, prevNode.row, prevNode.col]);
                    }
                }
            }
        }
        return newBoardGraph;
    }

    /// <summary>
    /// Checks if the given spawn location is valid.
    /// A valid spawn location is one that will not completely block player paths,
    /// that is, the last layer of the graph must have at least one connection to the previous layer.
    /// Bullets can only spawn on the extreme edges of the board and direct into the board.
    /// Modifies the board graph to remove connections
    /// </summary>
    /// <param name="boardGraph"></param>
    /// <param name="posIdx"></param>
    /// <returns>Returns true if the given position is a valid spawn, false otherwise</returns>
    private bool IsValidSpawn(ref BoardNode[,,] boardGraph, in Vector2 posIdx)
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

        BoardNode[,,] oldGraph = boardGraph.Clone() as BoardNode[,,];
        Queue<BoardNode> queue = new Queue<BoardNode>();
        // Find all the nodes that the bullet will be on
        // and remove all connections to the previous turn
        for (int turn = 0; turn < boardGraph.GetLength(0); turn++)
        {
            Vector2 currentPos = posIdx + direction * turn;
            BoardNode currentNode = boardGraph[turn, (int)currentPos.y, (int)currentPos.x];
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

        return isBoardSolvable(boardGraph);
    }

    /// <summary>
    /// Converts a 2D array index to a 2D position on the board.
    /// (0,0) is the center of the board,
    /// (0,0) is the top left of the array.
    /// </summary>
    /// <param name="idx"></param>
    /// <param name="size"></param>
    /// <returns>Returns the position on the board</returns>
    /* Example: size = 3
    *   (0,0) (1,0) (2,0)    (-1,1)  (0,1)  (1,1)
    *   (0,1) (1,1) (2,1) -> (-1,0)  (0,0)  (1,0)
    *   (0,2) (1,2) (2,2)    (-1,-1) (0,-1) (1,-1)
    */
    private Vector2 IndexToPosition(in Vector2 idx, in int size)
    {
        int center = size / 2;
        return new Vector2(idx.x - center, center - idx.y);
    }

    /// <summary>
    /// Converts a 2D position on the board to a 2D array index.
    /// (0,0) is the center of the board,
    /// (0,0) is the top left of the array.
    /// </summary>
    /// <param name="pos"></param>
    /// <param name="size"></param>
    /// <returns>Returns the 2D array index as a Vector2, where the x is the col and y is the row</returns>
    /* Example: size = 3
    *   (-1,1)  (0,1)  (1,1)     (0,0) (1,0) (2,0)
    *   (-1,0)  (0,0)  (1,0)  -> (0,1) (1,1) (2,1)
    *   (-1,-1) (0,-1) (1,-1)    (0,2) (1,2) (2,2)
    */
    private Vector2 PositionToIndex(in Vector2 pos, in int size)
    {
        int center = size / 2;
        return new Vector2(pos.x + center, center - pos.y);
    }
}
