﻿using System.Globalization;
using Iced.Intel;
using Newtonsoft.Json;

namespace OpcodeFinder;

internal class OpcodeFinder
{
    private const int RawDataSize = 0xC00;
    private readonly byte[] _arrayData;

    private readonly List<int> _offsetList = new()
                                             {
                                                 0,
                                                 RawDataSize,
                                                 -RawDataSize
                                             };

    private readonly Dictionary<string, string> _output = new();

    private readonly SigScanner _scanner;
    private readonly List<SignatureInfo> _signatures;

    public OpcodeFinder()
    {
        var config = ConfigReader.Load();

        if (!File.Exists(config.GamePath))
            throw new FileNotFoundException($"Cannot find ffxiv_dx11.exe. Your path from config.json: {config.GamePath}");

        _arrayData = File.ReadAllBytes(config.GamePath);

        _scanner = new SigScanner(_arrayData);

        _signatures = config.Signatures;
    }

    public void Find()
    {
        foreach (var signature in _signatures)
        {
            if (signature.SubInfo == null)
            {
                ProcessOffsetMethod(signature);
                continue;
            }

            ProcessJumpTableMethod(signature);
        }
    }

    public void SaveOutput()
    {
        File.WriteAllText("./output.json", JsonConvert.SerializeObject(_output, Formatting.Indented));
    }

    private void ProcessOffsetMethod(SignatureInfo signature)
    {
        var results = _scanner.FindPattern(signature.Signature);
        if (results.Count == 0)
        {
            Console.WriteLine($"[x] Faile to find signature for {signature.Name}");
            return;
        }

        foreach (var result in results)
        {
            ulong offset = 0;
            switch (signature.ReadType)
            {
                case ReadType.None:
                    break;
                case ReadType.Uint8:
                {
                    offset = _scanner.ReadByte((int)result + signature.Offset);
                    break;
                }
                case ReadType.Uint16:
                {
                    offset = _scanner.ReadUInt16((int)result + signature.Offset);
                    break;
                }
                case ReadType.Uint32:
                {
                    offset = _scanner.ReadUInt32((int)result + signature.Offset);
                    break;
                }
                case ReadType.Uint64:
                {
                    offset = _scanner.ReadUInt64((int)result + signature.Offset);
                    break;
                }
                default:
                {
                    Console.WriteLine($"[x] {signature.Name} has invalid ReadType.");
                    _output.TryAdd(signature.Name, "N/A");
                    continue;
                }
            }

            Console.WriteLine($"[+] {signature.Name}: 0x{offset:X}");
            _output.TryAdd(signature.Name, $"0x{offset:X}");
        }
    }

    private void ProcessJumpTableMethod(SignatureInfo signature)
    {
        if (signature.JumpTableType == JumpTableType.None)
        {
            Console.WriteLine($"[x] JumpTableType of {signature.Name} is 0. But it has SubInfo??");
            return;
        }

        var results = _scanner.FindPattern(signature.Signature);
        switch (results.Count)
        {
            case 0:
                Console.WriteLine($"[x] Signature for {signature.Name} has no result. Please update the signature.");
                return;
            case > 1:
                Console.WriteLine($"[x] Signature for {signature.Name} has more than 1 result. Please update the signature to make sure it is unique.");
                return;
        }

        Console.WriteLine($"\n[-] Finding OpCodes from {signature.Name}");

        var address = results[0];
        
        // PreProcess address
        {
            if (signature.ActionType == ActionType.Relative)
            {
                address = (ulong)_scanner.ReadCallSig((int)address);
            }
            else if (signature.FindCall)
            {
                var foundCall = false;

                if (signature.ActionType == ActionType.CrossReference)
                {
                    var references = _scanner.GetCrossReference((int)address);
                    foreach (var reference in references)
                    {
                        // Console.WriteLine($"0x{reference:X}");
                        if (foundCall)
                            break;
                        for (var offset = 1; offset <= 0x10; offset++)
                        {
                            var opcode = _scanner.ReadByte((int)(reference - (ulong)offset));
                            // Console.WriteLine($"0x{reference - (ulong)offset:X} / 0x{opcode:X}");
                            if (opcode != 0xE8)
                                continue;
                            foundCall = true;
                            address = (ulong)_scanner.ReadCallSig((int)reference - offset);
                            // Console.WriteLine($"Found. 0x{_scanner.ReadCallSig((int)reference - offset):X}");
                            break;
                        }
                    }
                }
                else
                {
                    for (var offset = 1; offset <= 0x10; offset++)
                    {
                        var opcode = _scanner.ReadByte((int)(address - (ulong)offset));
                        if (opcode != 0xE8)
                            continue;
                        foundCall = true;
                        address = (ulong)_scanner.ReadCallSig((int)address - offset);
                        break;
                    }
                }

                if (!foundCall)
                {
                    Console.WriteLine($"[x] Failed to find call function for {signature.Name}.");
                    return;
                }
            }
        }

        var bytes = _scanner.ReadBytes((int)address, signature.FunctionSize);
        var codeReader = new ByteArrayCodeReader(bytes);
        var decoder = Decoder.Create(64, codeReader);
        decoder.IP = address;
        var tableInfos = new List<TableInfo>();

        if (signature.JumpTableType == JumpTableType.SimpleSwitchCase)
            ProcessSimpleSwitchCase(codeReader, decoder, ref tableInfos);
        else
            ProcessJumpTable(signature, codeReader, decoder, ref tableInfos);

        if (tableInfos.Count == 0)
        {
            foreach (var info in signature.SubInfo)
                _output.TryAdd(info.Name, "N/A");

            return;
        }
        
        /*foreach (var info in tableInfos)
        {
            Console.WriteLine($"0x{info.Index:X} | 0x{info.Location:X}");
        }*/

        FindOpcodeFromJumpTable(signature, tableInfos);
    }

    private void ProcessSimpleSwitchCase(ByteArrayCodeReader codeReader, Decoder decoder, ref List<TableInfo> info)
    {
        ulong originalCase = 0;
        ulong currentCase = 0;

        // Console.WriteLine($"BaseAddress: 0x{baseAddress:X}");

        Instruction? lastInsturction = null;

        while (codeReader.CanReadByte)
        {
            decoder.Decode(out var instr);

            if (instr.OpCode.OpCode == 0XCC)
                break;

            /*var instrString = instr.ToString();*/

            // Console.WriteLine($"0x{instr.Immediate32:X} / {instr.OpCode.Code} / {instrString} / 0x{instr.NearBranch32:X}");

            if (lastInsturction == null)
            {
                lastInsturction = instr;
                continue;
            }

            switch (lastInsturction.Value.OpCode.Code)
            {
                case Code.Sub_rm32_imm32:
                    if (originalCase == lastInsturction.Value.Immediate32 && instr.OpCode.Code == Code.Je_rel8_64)
                    {
                        /*currentCase += instr.Immediate32;*/

                        info.Add(new TableInfo
                                 {
                                     Index = (int)currentCase,
                                     Location = instr.NearBranch32
                        });

                        // Console.WriteLine($" ddddd | Code.Jne_rel32_64: {instr.Length} / CurrentCase: 0x{currentCase:X} / 0x{instr.NearBranch32:X}");
                    }

                    break;
                // .text: 00000001406BF668 41 81 FA EC 01 00 00                                            cmp r10d, 1ECh
                case Code.Cmp_rm32_imm32 or Code.Cmp_rm32_imm8:
                    switch (instr.OpCode.Code)
                    {
                        // .text:00000001406BF66F 0F 87 80 00 00 00                                            ja      loc_1406BF6F5
                        case Code.Ja_rel32_64:
                            originalCase = lastInsturction.Value.Immediate32;
                            // Console.WriteLine($"NewLocation(start+offset): 0x{instr.NearBranch16:X}");
                            break;
                        // last case in this switch case
                        // .text:00000001406BF69F 41 83 FA 32                                                     cmp r10d, 32h; '2'
                        // .text:00000001406BF6A3 0F 85 AB 00 00 00                                               jnz loc_1406BF754
                        // .text:00000001406BF6A9 48 C7 44 24 20 60 00 00 00                                      mov[rsp + 38h + var_18], 60h
                        case Code.Jne_rel32_64 or Code.Jne_rel8_64:

                            var bytes = new byte[5];
                            for (var i = 0; i < 5; i++)
                                bytes[i] = _arrayData[i + instr.NearBranch32];

                            if (bytes[3] == 0x38 && bytes[4] == 0xC3)
                                break;

                            currentCase += lastInsturction.Value.Immediate32;
                            info.Add(new TableInfo
                                     {
                                         Index = (int)currentCase,
                                         Location = instr.NearBranch32
                                     });

                            /*Console.WriteLine($" aaa | Code.Jne_rel32_64: {instr.Length} / CurrentCase: 0x{currentCase:X} / 0x{instr.NearBranch32:X}");*/
                            break;
                    }

                    break;
                // If the last insturction is Je or Return(the beginning of new location)
                case Code.Je_rel8_64 or Code.Retnq:
                {
                    if (instr.OpCode.Code is Code.Sub_rm32_imm32 or Code.Sub_rm32_imm8 or Code.Cmp_rm32_imm8)
                    {
                        if (lastInsturction.Value.OpCode.Code is Code.Retnq)
                        {
                            originalCase = currentCase = instr.Immediate32;
                            // Console.WriteLine($"NewLocation(start+offset) Retnq: 0x{instr.Immediate32:X}");
                        }
                        else
                        {
                            currentCase += instr.Immediate32;
                            if (info.FindAll(i => i.Location == lastInsturction.Value.NearBranch32).Count > 0)
                                break;
                            info.Add(new TableInfo
                                     {
                                         Index = (int)currentCase,
                                         Location = lastInsturction.Value.NearBranch32
                                     });

                            // Console.WriteLine($" bbb | instr.Length: {instr.Length} / CurrentCase: 0x{currentCase:X} / 0x{instr.NearBranch32:X} / 0x{instr.Immediate32:X}");
                        }
                    }

                    break;
                }
                // cases that aint covered from above
                default:
                {
                    if (lastInsturction.Value.Code is Code.Jne_rel32_64 or Code.Jne_rel8_64 or Code.Movzx_r32_rm16 && instr.OpCode.Code is Code.Mov_rm64_imm32 or Code.Sub_rm32_imm32)
                    {
                        if (instr.OpCode.Code is Code.Sub_rm32_imm32)
                        {
                            currentCase += instr.Immediate32;
                            originalCase = instr.Immediate32;

                            lastInsturction = instr;
                            continue;
                        }

                        info.Add(new TableInfo
                                 {
                                     Index = (int)currentCase,
                                     Location = instr.IP
                                 });
                        // Console.WriteLine($" ccc | Code.Jne_rel32_64: {instr.Length} / CurrentCase: 0x{currentCase:X} / 0x{instr.NearBranch32:X}");
                    }

                    break;
                }
            }

            lastInsturction = instr;
        }
    }

    private void ProcessJumpTable(SignatureInfo signature, ByteArrayCodeReader codeReader, Decoder decoder, ref List<TableInfo> tableInfos)
    {
        var subs = new List<int>();
        var indirectTables = new List<ulong>();
        var jumpTables = new List<ulong>();

        while (codeReader.CanReadByte)
        {
            decoder.Decode(out var instr);

            if (instr.OpCode.OpCode == 0XCC)
                break;

            var instrString = instr.ToString();

            if (instrString.StartsWith("sub eax,"))
            {
                var subValue = instrString.Substring(instrString.IndexOf(',') + 1).Replace("h", "");
                subs.Add(int.Parse(subValue, NumberStyles.HexNumber));
                continue;
            }

            if (instrString.StartsWith("lea eax,["))
            {
                var number = instrString.Substring(instrString.LastIndexOf('-') + 1).Replace("]", "").Replace("h", "");
                subs.Add(int.Parse(number, NumberStyles.HexNumber));
                continue;
            }

            if (instrString.StartsWith("movzx eax,byte ptr [rdx+rax+"))
            {
                indirectTables.Add(instr.NearBranch64 - RawDataSize);
                continue;
            }

            if (instrString.StartsWith("mov ecx,[") && instrString.Contains("+rax*4+")) jumpTables.Add(instr.NearBranch64 - RawDataSize);
        }

        if (jumpTables.Count == 0)
        {
            Console.WriteLine($"[x] No jumptable was found for {signature.Name}.");
            return;
        }

        if (signature.JumpTableType == JumpTableType.Indirect && indirectTables.Count != jumpTables.Count)
        {
            Console.WriteLine($"[x] The size of indirect table doesn't match to jump table. Function: {signature.Name}");
            return;
        }

        var startIndex = signature.JumpTableType == JumpTableType.Indirect ? 1 : 0;

        for (var i = startIndex; i < jumpTables.Count; i++)
        {
            var idx = signature.JumpTableType == JumpTableType.Indirect ? i - 1 : i;

            var minimumCaseValue = subs[idx];
            var jumpTable = jumpTables[idx];

            switch (signature.JumpTableType)
            {
                case JumpTableType.Indirect:
                {
                    var indirectTable = indirectTables[idx];
                    var delta = jumpTables[i] - indirectTable;

                    for (var j = minimumCaseValue; j < minimumCaseValue + (int)delta; j++)
                    {
                        var jumpTableIndex = j - minimumCaseValue;
                        var tableByte = _arrayData[(int)indirectTable + jumpTableIndex];

                        var location = BitConverter.ToInt32(_arrayData, (int)jumpTable + tableByte * 4) - RawDataSize;

                        tableInfos.Add(new TableInfo
                                       {
                                           Index = j,
                                           Location = (ulong)location
                                       });
                    }

                    break;
                }
                case JumpTableType.Direct:
                {
                    for (var j = minimumCaseValue;; j++)
                    {
                        var jumpTableIndex = j - minimumCaseValue;
                        var tableAddress = (int)jumpTable + jumpTableIndex * 4;
                        var tableByte1 = _arrayData[tableAddress];
                        var tableByte2 = _arrayData[tableAddress + 1];
                        if (tableByte1 == 0xCC && tableByte2 == 0xCC)
                            break;

                        var location = BitConverter.ToInt32(_arrayData, tableAddress) - RawDataSize;
                        tableInfos.Add(new TableInfo
                                       {
                                           Index = j,
                                           Location = (ulong)location
                                       });
                        // Console.WriteLine($"DirectTable#{jumpTableIndex}({j}) 0x{location:X}");
                    }

                    break;
                }
            }
        }
    }

    private void FindOpcodeFromJumpTable(SignatureInfo signature, List<TableInfo> tableInfos)
    {
        foreach (var subSignature in signature.SubInfo)
        {
            if (subSignature.ByteMatch)
            {
                if (subSignature.ByteLength <= 0)
                {
                    Console.WriteLine($"[x] Found ByteMatch in {subSignature.Name} but ByteLength is <= 0.");
                    continue;
                }

                var pattern = SigScanner.HexToBytes(subSignature.Signature).GetRange(0, subSignature.ByteLength);

                foreach (var tableInfo in tableInfos)
                {
                    var bytes = _scanner.ReadBytes((int)tableInfo.Location, subSignature.ByteLength);
                    if (SigScanner.ByteMatch(bytes, 0, pattern))
                    {
                        Console.WriteLine($"[+] {subSignature.Name}: 0x{tableInfo.Index:X}");
                        break;
                    }
                }

                continue;
            }

            var results = _scanner.FindPattern(subSignature.Signature);

            switch (subSignature.ActionType)
            {
                case ActionType.None:
                {
                    var skip = false;
                    for (var offset = 0; offset <= 0x50; offset++)
                    {
                        var filteredResults = results.SelectMany(result => tableInfos.Where(info => info.Location == result - (ulong)offset));
                        if (!filteredResults.Any())
                            continue;
                        var opcodeStr = $"{filteredResults.Aggregate("", (current, info) => current + $"0x{info.Index:X} ")}";
                        opcodeStr = opcodeStr[..^1];
                        
                        Console.WriteLine($"[+] {(filteredResults.Count() > 1 ? "Possible opcodes for " : "")}{subSignature.Name}: {opcodeStr}");
                        _output.TryAdd(subSignature.Name, opcodeStr);
                        skip = true;
                        break;
                    }

                    /*foreach (var @ulong in results)
                    {
                        Console.WriteLine($"|| 0x{@ulong:X}");
                    }*/

                    if (!skip)
                    {
                        _output.TryAdd(subSignature.Name, "N/A");

                        Console.WriteLine($"[x] Cannot find opcode for {subSignature.Name}");
                    }

                    continue;
                }
                case ActionType.ReadThenCrossReference:
                {
                    // Read value first
                    foreach (var result in results)
                    {
                        ulong value = 0;
                        switch (subSignature.ReadType)
                        {
                            case ReadType.None:
                                break;
                            case ReadType.Uint8:
                            {
                                value = _arrayData[(int)result + subSignature.Offset];
                                break;
                            }
                            case ReadType.Uint16:
                            {
                                value = BitConverter.ToUInt16(_arrayData, (int)result + subSignature.Offset);
                                break;
                            }
                            case ReadType.Uint32:
                            {
                                value = BitConverter.ToUInt32(_arrayData, (int)result + subSignature.Offset);
                                break;
                            }
                            case ReadType.Uint64:
                            {
                                value = BitConverter.ToUInt64(_arrayData, (int)result + subSignature.Offset);
                                break;
                            }
                            default:
                            {
                                Console.WriteLine($"[x] {subSignature.Name} has invalid ReadType.");
                                _output.TryAdd(subSignature.Name, "N/A");

                                continue;
                            }
                        }

                        var name = "";

                        if (subSignature.DesiredValues != null && subSignature.DesiredValues.TryGetValue((int)value, out var v))
                            name = v;

                        // Finding the start of the function
                        var functionStart = result;
                        for (var i = 0; i <= 0x50; i++)
                        {
                            if (_arrayData[functionStart - (ulong)i] != 0xCC)
                                continue;
                            functionStart -= (ulong)(i - 1);
                            break;
                        }

                        // Finding References
                        var xrefResults = new List<TableInfo>();
                        var xrefs = _scanner.GetCrossReference((int)functionStart);
                        foreach (var i in _offsetList)
                        {
                            foreach (var xref in xrefs)
                            {
                                for (var offset = 0; offset <= 0x50; offset++)
                                {
                                    var curAddress = xref - (ulong)offset + (ulong)i;
                                    var info = tableInfos.Find(info => info.Location == curAddress);
                                    if (info.Index == 0)
                                        continue;

                                    xrefResults.Add(info);
                                    if (!subSignature.HasMultipleResult)
                                        break;
                                }

                                if (!subSignature.HasMultipleResult && xrefResults.Count != 0)
                                    break;
                            }

                            if (xrefResults.Count != 0)
                                break;
                        }

                        if (xrefResults.Count == 0)
                        {
                            Console.Write($"[x] Cannot find opcode for {subSignature.Name}{name}. ");
                            Console.Write($"Signatures: {results.Count}. {results.Aggregate("", (current, address) => current + $"0x{address:X} ")}");
                            Console.WriteLine($" / xRefs: {xrefs.Count}. {xrefs.Aggregate("", (current, xref) => current + $"0x{xref:X} ")}");
                            _output.TryAdd(subSignature.Name + name, "N/A");

                            continue;
                        }

                        var opcodeStr = @$"{xrefResults.Aggregate("", (current, xrefResult) => current + $"0x{xrefResult.Index:X} ")}";
                        opcodeStr = opcodeStr[..^1];

                        _output.TryAdd(subSignature.Name + name, opcodeStr);
                        Console.WriteLine($"[+] {subSignature.Name}{name}: {opcodeStr}");
                    }

                    break;
                }
                case ActionType.CrossReference:
                {
                    var xrefResults = new List<TableInfo>();
                    string? opcodeStr;
                    if (subSignature.ReferenceCount != null)
                    {
                        if (results.Count > 1)
                        {
                            Console.WriteLine($"[x] The signature for {subSignature.Name} has multiple results while ReferenceCount is not empty");
                            _output.TryAdd(subSignature.Name, "N/A");
                            continue;
                        }

                        var xrefs = _scanner.GetCrossReference((int)results[0], (int)subSignature.ReferenceCount);
                        if (xrefs == 0)
                        {
                            Console.WriteLine($"[x] No references was found for {subSignature.Name}");
                            _output.TryAdd(subSignature.Name, "N/A");

                            continue;
                        }

                        foreach (var address in _offsetList.Select(i => xrefs + (ulong)i))
                        {
                            for (var offset = 0; offset <= 0x50; offset++)
                            {
                                var curAddress = address - (ulong)offset;
                                var info = tableInfos.Find(i => i.Location == curAddress);
                                if (info.Index == 0)
                                    continue;

                                xrefResults.Add(info);
                                if (!subSignature.HasMultipleResult)
                                    break;
                            }

                            if (xrefResults.Count != 0)
                                break;
                        }

                        if (xrefResults.Count == 0)
                        {
                            Console.WriteLine($"[x] Cannot find opcode for {subSignature.Name}. Signature result count: {results.Count} / 0x{results[0]:X}");
                            _output.TryAdd(subSignature.Name, "N/A");

                            continue;
                        }

                        opcodeStr = $"{xrefResults.Aggregate("", (current, xrefResult) => current + $"0x{xrefResult.Index:X} ")}";
                        opcodeStr = opcodeStr[..^1];

                        _output.TryAdd(subSignature.Name,
                                       opcodeStr);
                        Console.WriteLine($"[+] {subSignature.Name}: {opcodeStr}");

                        continue;
                    }

                    foreach (var offsetedXRef in from magicOffset in _offsetList
                                                 from result in results
                                                 let xrefs = _scanner.GetCrossReference((int)result)
                                                 from xref in xrefs
                                                 select xref + (ulong)magicOffset)
                    {
                        for (var offset = 0; offset <= 0x50; offset++)
                        {
                            var curAddress = offsetedXRef - (ulong)offset;
                            var info = tableInfos.Find(i => i.Location == curAddress);
                            if (info.Index == 0)
                                continue;

                            xrefResults.Add(info);
                            break;
                        }

                        if (xrefResults.Count != 0)
                            break;
                    }

                    if (xrefResults.Count == 0)
                    {
                        Console.WriteLine($"[x] Cannot find opcode for {subSignature.Name}. Signature result count: {results.Count} / 0x{results[0]:X} /  0x{results[1]:X}");
                        _output.TryAdd(subSignature.Name, "N/A");

                        continue;
                    }

                    opcodeStr = $"{xrefResults.Aggregate("", (current, xrefResult) => current + $"0x{xrefResult.Index:X} ")}";
                    opcodeStr = opcodeStr[..^1];
                    _output.TryAdd(subSignature.Name,
                                   opcodeStr
                                  );
                    Console.WriteLine($"[+] {subSignature.Name}: {opcodeStr}");

                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private struct TableInfo
    {
        public int Index;
        public ulong Location;
    }
}