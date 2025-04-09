using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Profiling;

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

    struct BoardNode
    {
        public static int Mask = 0x0F; // 4 bits each for the row and col

        // Shift the bits to the right and mask them to get the direction
        //   Up        Left      Down      Right
        //   Row  Col  Row  Col  Row  Col  Row  Col
        // 0b0000 0000 0000 0000 0000 0000 0000 0000
        // Every byte is the row and col of the node it is connected to

        // Connections to nodes on the previous turn
        public int prev;

        // Connections to nodes on the next turn
        public int next;

        public (int row, int col) GetConnection(bool next, int direction)
        {
            int connections = next ? this.next : this.prev;
            // Shift the bits to the right and mask them to get the direction
            // 0 = up, 1 = left, 2 = down, 3 = right
            // Every byte is the col and row of the node it is connected to
            int row = (connections >> (direction * 8 + 4)) & Mask;
            int col = (connections >> (direction * 8)) & Mask;
            if (row == 0 && col == 0)
            {
                return (-1, -1); // No connection
            }
            return (row, col);
        }

        public void AddConnection(bool next, int direction, int row, int col)
        {
            if (next)
            {
                this.next |= (((row & Mask) << 4) | (col & Mask)) << (direction * 8);
            }
            else
            {
                this.prev |= (((row & Mask) << 4) | (col & Mask)) << (direction * 8);
            }
        }

        public void RemoveConnection(bool next, int direction, int row, int col)
        {
            int mask = 0xFF << (direction * 8);
            if (next)
            {
                this.next &= ~mask;
            }
            else
            {
                this.prev &= ~mask;
            }
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
    public ValueInput iterative; // when true, the algorithm will use an iterative approach to find the bullet spawns

    [DoNotSerialize]
    public ValueInput numBulletsToSpawn;

    [DoNotSerialize]
    public ValueOutput bulletsToSpawn;

    private List<Bullet> outputBullets;
    private static Vector2Int[] directions = new Vector2Int[4]
    {
        new Vector2Int(0, -1), // up
        new Vector2Int(-1, 0), // left
        new Vector2Int(0, 1), // down
        new Vector2Int(1, 0), // right
    };

    protected override void Definition()
    {
        inputTrigger = ControlInput(
            "SpawnBullets",
            (flow) =>
            {
                // Profiler.BeginSample("BulletSpawner");
                Vector2Int playerPos = Vector2Int.RoundToInt(
                    flow.GetValue<Vector2>(playerPosition)
                );
                List<Bullet> bulletData = flow.GetValue<List<Bullet>>(bullets);
                int size = flow.GetValue<int>(boardSize);
                int numBullets = flow.GetValue<int>(numBulletsToSpawn);
                bool doIterative = flow.GetValue<bool>(iterative);
                outputBullets = GetBulletSpawns(
                    GameDataToBoard(playerPos, bulletData, size),
                    numBullets,
                    doIterative
                );
                // Profiler.EndSample();
                return outputTrigger;
            }
        );
        outputTrigger = ControlOutput("Output");

        playerPosition = ValueInput<Vector2>("playerPosition", Vector2Int.zero);
        bullets = ValueInput<List<Bullet>>("bullets", new List<Bullet>());
        boardSize = ValueInput<int>("boardSize", 7);
        numBulletsToSpawn = ValueInput<int>("numBulletsToSpawn", 1);
        iterative = ValueInput<bool>("iterative", false);
        bulletsToSpawn = ValueOutput<List<Bullet>>("bulletsToSpawn", (flow) => outputBullets);

        Succession(inputTrigger, outputTrigger);
        Assignment(inputTrigger, bulletsToSpawn);
    }

    private int[,] GameDataToBoard(in Vector2Int playerPos, in List<Bullet> bulletData, in int size)
    {
        int[,] board = new int[size, size];
        // Set the player position
        Vector2Int playerIdx = PositionToIndex(playerPos, size);
        board[playerIdx.y, playerIdx.x] |= (int)BoardState.Player;
        // Set the bullet positions on the board
        foreach (Bullet bullet in bulletData)
        {
            Vector2Int posIdx = PositionToIndex(Vector2Int.RoundToInt(bullet.position), size);
            int row = posIdx.y;
            int col = posIdx.x;
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
    /// <param name="doIterative"></param>
    /// <returns>Returns a list of valid bullets to spawn of max size numBulletsToSpawn</returns>
    /// <exception cref="Exception"></exception>
    private List<Bullet> GetBulletSpawns(
        in int[,] board,
        in int numBulletsToSpawn,
        in bool doIterative = false
    )
    {
        // First, create a directed graph where each node is a tile and each layer is a turn.
        // A connection between two nodes is a valid move for the player that will not hit a bullet.
        // The array is indexed by (turn, row, col) or (turn, y, x)
        int boardSize = board.GetLength(0);
        // After boardSize - 2 turns, all bullets spawned on turn 0 will be gone. Add 1 for the current turn.
        int turnsToSim = boardSize - 1;
        BoardNode[,,] boardGraph = new BoardNode[turnsToSim, boardSize, boardSize];

        // Find the player
        int playerRow = -1;
        int playerCol = -1;
        for (int row = 0; row < boardSize; row++)
        {
            for (int col = 0; col < boardSize; col++)
            {
                if ((board[row, col] & (int)BoardState.Player) > 0)
                {
                    playerRow = row;
                    playerCol = col;
                    break;
                }
            }
        }
        if (playerRow == -1 || playerCol == -1)
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
        Queue<(int, int, int)> queue = new Queue<(int, int, int)>();
        queue.Enqueue((0, playerRow, playerCol));
        while (queue.Count > 0)
        {
            (int turn, int row, int col) = queue.Dequeue();
            BoardNode currentNode = boardGraph[turn, row, col];
            if (turn == turnsToSim - 1)
                continue; // Skip the last turn, we don't need to check it

            // Check the 4 adjacent tiles
            // 0 = up, 1 = left, 2 = down, 3 = right
            for (int d = 0; d < 4; d++)
            {
                Vector2Int moveDir = BulletSpawner.directions[d];
                int nextRow = row + moveDir.y;
                int nextCol = col + moveDir.x;

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
                currentNode.AddConnection(true, d, nextRow, nextCol);
                nextNode.AddConnection(false, (d + 2) % 4, row, col);
                boardGraph[turn + 1, nextRow, nextCol] = nextNode;
                queue.Enqueue((turn + 1, nextRow, nextCol));
            }
            boardGraph[turn, row, col] = currentNode;
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
        List<Vector2Int> spawnLocations = GetSpawnLocations(boardSize);
        List<int> spawnSet = new List<int>();
        if (doIterative)
        {
            // If the iterative approach is selected, we will use a different algorithm to find the bullet spawns
            // This algorithm will select a single spawn location from a set and continue to build on that
            // instead of trying to find all possible spawn locations at once.
          //  Debug.Log("Using iterative approach to find bullet spawns");
            spawnSet = GetSpawnSetIterative(boardGraph, spawnLocations, numBulletsToSpawn);
        }
        else
        {
          //  Debug.Log("Using choose approach to find bullet spawns");
            spawnSet = GetSpawnSetChoose(boardGraph, spawnLocations, numBulletsToSpawn);
        }

        // Convert the valid spawn sets to bullet objects
        List<Bullet> bullets = new List<Bullet>();
        foreach (int spawnIdx in spawnSet)
        {
            Vector2Int spawnPos = spawnLocations[spawnIdx];

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

                    Vector2Int direction = BulletSpawner.directions[d];
                    int nextRow = row + direction.y;
                    int nextCol = col + direction.x;
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
                if (boardGraph[lastTurn, row, col].prev > 0)
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
    private List<Vector2Int> GetSpawnLocations(in int size)
    {
        List<Vector2Int> spawnLocations = new List<Vector2Int>();
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
                spawnLocations.Add(new Vector2Int(col, row));
            }
        }
        return spawnLocations;
    }

    /// <summary>
    /// This function will attempt to find a set of bullet spawns
    /// for the specific board state up to the number of bullets specified.
    /// </summary>
    /// <param name="boardGraph"></param>
    /// <param name="spawnLocations"></param>
    /// <param name="numBulletsToSpawn"></param>
    /// <returns></returns>
    private List<int> GetSpawnSetIterative(
        in BoardNode[,,] boardGraph,
        in List<Vector2Int> spawnLocations,
        int numBulletsToSpawn
    )
    {
        List<int> spawnSet = new List<int>();
        BoardNode[,,] currentGraph = boardGraph.Clone() as BoardNode[,,];
        while (spawnSet.Count < numBulletsToSpawn)
        {
            // The list of possible spawn locations for the next bullet
            List<(int Idx, BoardNode[,,] Graph)> validBullets = new List<(int, BoardNode[,,])>();
            for (int i = 0; i < spawnLocations.Count; i++)
            {
                if (spawnSet.Contains(i))
                    continue; // Skip already selected spawn locations
                // Create a deep copy of the board graph for each bullet check
                BoardNode[,,] boardGraphCopy = currentGraph.Clone() as BoardNode[,,];
                // Check if the spawn location is valid
                if (IsValidSpawn(ref boardGraphCopy, spawnLocations[i]))
                {
                    // If it is valid, add it to the list of valid spawn locations
                    validBullets.Add((i, boardGraphCopy));
                }
            }
            if (validBullets.Count == 0)
            {
                // No more valid spawn locations, exit the loop
                break;
            }
            // TODO: Select based on a difficulty heuristic
            // Select a random spawn location from the list of valid spawn locations
            int randomIdx = UnityEngine.Random.Range(0, validBullets.Count);
            int spawnIdx = validBullets[randomIdx].Idx;
            spawnSet.Add(spawnIdx);
            currentGraph = validBullets[randomIdx].Graph;
        }
        Debug.Log("Spawning " + spawnSet.Count + " bullets");

        return spawnSet;
    }

    /// <summary>
    /// This function will attempt to find a set of bullet spawns
    /// for the specific board state up to the number of bullets specified.
    /// </summary>
    /// <param name="boardGraph"></param>
    /// <param name="spawnLocations"></param>
    /// <param name="numBulletsToSpawn"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private List<int> GetSpawnSetChoose(
        in BoardNode[,,] boardGraph,
        List<Vector2Int> spawnLocations,
        int numBulletsToSpawn
    )
    {
        // Each tuple represents a set of bullets that can be spawned and the board graph after spawning them
        var validSpawnSets = new List<(List<int> Set, BoardNode[,,] Graph)>();
        for (int i = 0; i < spawnLocations.Count; i++)
        {
            // Create a deep copy of the board graph for each spawn set
            BoardNode[,,] boardGraphCopy = boardGraph.Clone() as BoardNode[,,];
            // Check if the spawn location is valid
            if (IsValidSpawn(ref boardGraphCopy, spawnLocations[i]))
            {
                // If it is valid, add it to the list of valid spawn locations
                validSpawnSets.Add((new List<int> { i }, boardGraphCopy));
            }
        }

        if (validSpawnSets.Count == 0)
        {
            Debug.Log("No valid spawn locations found");
            return new List<int>();
        }

        // Add more bullets until you can't add any more
        while (validSpawnSets[0].Set.Count < numBulletsToSpawn)
        {
            var newValidSpawnSets = new List<(List<int> Set, BoardNode[,,] Graph)>();

            // For each valid spawn set, try each spawn location and see if it is valid.
            foreach (var setGraph in validSpawnSets)
            {
                var spawnSet = setGraph.Set;
                // look forward from the index of the last bullet in the spawn set to avoid duplicates
                for (int j = spawnSet[spawnSet.Count - 1] + 1; j < spawnLocations.Count; j++)
                {
                    // Create a deep copy of the board graph for each spawn set
                    BoardNode[,,] boardGraphCopy = setGraph.Graph.Clone() as BoardNode[,,];
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

            // Check if there are any new valid spawn sets
            if (newValidSpawnSets.Count == 0)
            {
                break;
            }
            validSpawnSets = newValidSpawnSets; // Update the valid spawn sets
        }

        // check that all the spawn sets have the same number of bullets
        foreach (var spawnSet in validSpawnSets)
        {
            if (spawnSet.Set.Count != validSpawnSets[0].Set.Count)
            {
                throw new Exception("Spawn sets have different number of bullets"); // This should never happen, but it is here for safety
            }
        }

      /*  Debug.Log(
            "Found "
                + validSpawnSets.Count
                + " valid spawn sets with "
                + validSpawnSets[0].Set.Count
                + " bullets"
        );*/
        // TODO: Select based on a difficulty heuristic
        int randomIdx = UnityEngine.Random.Range(0, validSpawnSets.Count);
        return validSpawnSets[randomIdx].Set;
    }

    /// <summary>
    /// Attempts to create new spawn sets by adding new spawn locations to the given spawn set.
    /// </summary>
    /// <param name="spawnSet"></param>
    /// <param name=""></param>
    /// <returns></returns>
    private List<(List<int> Set, BoardNode[,,] Graph)> AddToSpawnSet(
        in (List<int> Set, BoardNode[,,] Graph) spawnSet,
        in List<Vector2Int> spawnLocations
    )
    {
        var newSpawnSets = new List<(List<int> Set, BoardNode[,,] Graph)>();
        // look forward from the index of the last bullet in the spawn set
        // to avoid duplicates
        for (int j = spawnSet.Set[spawnSet.Set.Count - 1] + 1; j < spawnLocations.Count; j++)
        {
            // Create a deep copy of the board graph for each spawn set
            BoardNode[,,] boardGraphCopy = spawnSet.Graph.Clone() as BoardNode[,,];
            // Check if the spawn location is valid
            if (IsValidSpawn(ref boardGraphCopy, spawnLocations[j]))
            {
                // If it is valid, add it to the list of valid spawn locations
                List<int> newSpawnSet = new List<int>(spawnSet.Set);
                newSpawnSet.Add(j);
                newSpawnSets.Add((newSpawnSet, boardGraphCopy));
            }
        }
        return newSpawnSets;
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
    private bool IsValidSpawn(ref BoardNode[,,] boardGraph, in Vector2Int posIdx)
    {
        // determine the direction of the bullet
        Vector2Int direction = new Vector2Int(0, 0);
        int size = boardGraph.GetLength(1);
        if (posIdx.y == size - 1)
            direction = new Vector2Int(0, -1); // up
        else if (posIdx.x == size - 1)
            direction = new Vector2Int(-1, 0); // left
        else if (posIdx.y == 0)
            direction = new Vector2Int(0, 1); // down
        else
            direction = new Vector2Int(1, 0); // right

        // turn, row, col
        Queue<(int, int, int)> queue = new Queue<(int, int, int)>();
        // Remove connections for nodes that are no longer traversable
        for (int turn = 0; turn < boardGraph.GetLength(0); turn++)
        {
            Vector2Int currentPos = posIdx + direction * turn;
            BoardNode currentNode = boardGraph[turn, currentPos.y, currentPos.x];
            // Remove connections for nodes that the bullet will be on
            if (currentNode.prev > 0)
            {
                // Remove all connections to the previous turn
                for (int d = 0; d < 4; d++)
                {
                    (int prevRow, int prevCol) = currentNode.GetConnection(false, d);
                    if (prevRow == -1 || prevCol == -1)
                        continue;
                    BoardNode prevNode = boardGraph[turn - 1, prevRow, prevCol];
                    prevNode.RemoveConnection(true, (d + 2) % 4, currentPos.y, currentPos.x);
                    boardGraph[turn - 1, prevRow, prevCol] = prevNode;
                }
                currentNode.prev = 0;
                boardGraph[turn, currentPos.y, currentPos.x] = currentNode;
                queue.Enqueue((turn, currentPos.y, currentPos.x));
            }

            if (turn == boardGraph.GetLength(0) - 1)
                continue; // Skip the last turn, we don't need to check it

            // Check if the bullet will swap with a player
            Vector2Int adjPos = posIdx + direction * (turn + 1);
            BoardNode adjNode = boardGraph[turn, adjPos.y, adjPos.x];
            // Check if the adjacent tile on the current turn has connections to the bullets current position
            for (int d = 0; d < 4; d++)
            {
                (int nextRow, int nextCol) = adjNode.GetConnection(true, d);
                if (nextRow == currentPos.y && nextCol == currentPos.x)
                {
                    BoardNode nextNode = boardGraph[turn + 1, nextRow, nextCol];
                    nextNode.RemoveConnection(false, (d + 2) % 4, adjPos.y, adjPos.x);
                    adjNode.RemoveConnection(true, d, nextRow, nextCol);
                    boardGraph[turn + 1, nextRow, nextCol] = nextNode;
                    if (nextNode.prev == 0)
                    {
                        queue.Enqueue((turn + 1, nextRow, nextCol));
                    }
                }
            }
            boardGraph[turn, adjPos.y, adjPos.x] = adjNode;
        }

        // Remove all forward connections from the nodes the bullet will be on
        while (queue.Count > 0)
        {
            (int curTurn, int curRow, int curCol) = queue.Dequeue();
            BoardNode currentNode = boardGraph[curTurn, curRow, curCol];
            for (int d = 0; d < 4; d++)
            {
                (int nextRow, int nextCol) = currentNode.GetConnection(true, d);
                if (nextRow == -1 || nextCol == -1)
                    continue;
                BoardNode nextNode = boardGraph[curTurn + 1, nextRow, nextCol];
                nextNode.RemoveConnection(false, (d + 2) % 4, curRow, curCol);
                boardGraph[curTurn + 1, nextRow, nextCol] = nextNode;
                if (nextNode.prev == 0)
                {
                    queue.Enqueue((curTurn + 1, nextRow, nextCol)); // add to queue if it has no more connections
                }
            }
            currentNode.prev = 0;
            boardGraph[curTurn, curRow, curCol] = currentNode;
        }

        return isBoardSolvable(boardGraph);
    }

    private List<Bullet> IndicesToBullets(
        in List<int> spawnSet,
        in List<Vector2Int> spawnLocations,
        in int boardSize
    )
    {
        List<Bullet> bullets = new List<Bullet>();
        foreach (int spawnIdx in spawnSet)
        {
            Vector2Int spawnPos = spawnLocations[spawnIdx];

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
    private Vector2Int IndexToPosition(in Vector2Int idx, in int size)
    {
        int center = size / 2;
        return new Vector2Int(idx.x - center, center - idx.y);
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
    private Vector2Int PositionToIndex(in Vector2Int pos, in int size)
    {
        int center = size / 2;
        return new Vector2Int(pos.x + center, center - pos.y);
    }
}
