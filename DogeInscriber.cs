using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using NBitcoin;
using NBitcoin.Altcoins;
using System.Net.Http;
using Newtonsoft.Json;
using NBitcoin.Crypto;

public class DogeInscriber
{
    private Network network;
    private Key privateKey;
    private BitcoinSecret bitcoinSecret;
    private BitcoinAddress address;
    private List<Coin> utxos;

    private const int MAX_CHUNK_LEN = 240;
    private const int MAX_PAYLOAD_LEN = 1500;
    private const int FEE_PER_KB = 10000000; // Настройте по необходимости

    public DogeInscriber(string wif = null)
    {
        // Инициализируем сеть Dogecoin
        network = Dogecoin.Instance.Mainnet;

        if (!string.IsNullOrEmpty(wif))
        {
            bitcoinSecret = new BitcoinSecret(wif, network);
            privateKey = bitcoinSecret.PrivateKey;
        }
        else
        {
            privateKey = new Key(); // Генерируем новый приватный ключ
            bitcoinSecret = privateKey.GetBitcoinSecret(network);
        }

        address = bitcoinSecret.GetAddress(ScriptPubKeyType.Legacy);
        utxos = new List<Coin>();
    }

    /// <summary>
    /// Возвращает адрес кошелька.
    /// </summary>
    public string GetAddress()
    {
        return address.ToString();
    }

    /// <summary>
    /// Возвращает приватный ключ в формате WIF.
    /// </summary>
    public string GetPrivateKey()
    {
        return bitcoinSecret.ToWif();
    }

    /// <summary>
    /// Синхронизирует кошелек, получая UTXO из блокчейна Dogecoin.
    /// </summary>
    public async Task SyncWalletAsync()
    {
        string url = $"https://wallet-api.dogeord.io/address/btc-utxo?address={address}";

        using (HttpClient client = new HttpClient())
        {
            var response = await client.GetAsync(url);
            string json = await response.Content.ReadAsStringAsync();
            dynamic result = JsonConvert.DeserializeObject(json);

            if (result.result != null)
            {
                utxos.Clear();

                foreach (var output in result.result)
                {
                    var txid = new uint256((string)output.txId);
                    int vout = (int)output.outputIndex;
                    var scriptPubKey = Script.FromHex((string)output.scriptPk);
                    Money amount = Money.Satoshis((long)output.satoshis);

                    Coin coin = new Coin(new OutPoint(txid, vout), new TxOut(amount, scriptPubKey));
                    utxos.Add(coin);
                }
            }
            else
            {
                throw new Exception("Не удалось получить UTXO.");
            }
        }
    }

    /// <summary>
    /// Возвращает общий баланс кошелька.
    /// </summary>
    public Money GetBalance()
    {
        return utxos.Sum(coin => coin.Amount);
    }

    /// <summary>
    /// Записывает данные в блокчейн Dogecoin.
    /// </summary>
    /// <param name="contentType">Тип контента (например, "text/plain").</param>
    /// <param name="data">Данные для записи.</param>
    /// <param name="receiverAddress">Адрес получателя Dogecoin.</param>
    public async Task<string> InscribeAsync(string contentType, byte[] data, string receiverAddress)
    {
        if (string.IsNullOrEmpty(contentType))
            throw new ArgumentNullException(nameof(contentType));

        if (data == null || data.Length == 0)
            throw new ArgumentNullException(nameof(data));

        if (string.IsNullOrEmpty(receiverAddress))
            throw new ArgumentNullException(nameof(receiverAddress));

        BitcoinAddress receiver = BitcoinAddress.Create(receiverAddress, network);

        // Разбиваем данные на части
        List<byte[]> parts = new List<byte[]>();
        int index = 0;
        while (index < data.Length)
        {
            int length = Math.Min(MAX_CHUNK_LEN, data.Length - index);
            parts.Add(data.Skip(index).Take(length).ToArray());
            index += length;
        }

        // Создаем список операций для инсрипции
        List<Op> inscriptionOps = new List<Op>();
        inscriptionOps.Add(Op.GetPushOp(Encoding.UTF8.GetBytes("ord")));
        inscriptionOps.Add(Op.GetPushOp(EncodeNumber(parts.Count)));
        inscriptionOps.Add(Op.GetPushOp(Encoding.UTF8.GetBytes(contentType)));

        for (int i = 0; i < parts.Count; i++)
        {
            inscriptionOps.Add(Op.GetPushOp(EncodeNumber(parts.Count - i - 1)));
            inscriptionOps.Add(Op.GetPushOp(parts[i]));
        }

        List<Transaction> txs = new List<Transaction>();
        Script lastLockScript = null;
        Script lastPartialScript = null;
        Coin p2shCoin = null;
        int currentOpIndex = 0;

        while (currentOpIndex < inscriptionOps.Count)
        {
            // Создаем частичный скрипт
            List<Op> partialOps = new List<Op>();

            if (txs.Count == 0 && currentOpIndex < inscriptionOps.Count)
            {
                partialOps.Add(inscriptionOps[currentOpIndex]);
                currentOpIndex++;
            }

            while (currentOpIndex < inscriptionOps.Count)
            {
                partialOps.Add(inscriptionOps[currentOpIndex]);
                currentOpIndex++;

                Script tempScript = new Script(partialOps.ToArray());
                if (tempScript.ToBytes(true).Length > MAX_PAYLOAD_LEN)
                {
                    partialOps.RemoveAt(partialOps.Count - 1);
                    currentOpIndex--;
                    break;
                }
            }

            Script partialScript = new Script(partialOps.ToArray());

            // Создаем lock script
            List<Op> lockOps = new List<Op>();
            lockOps.Add(Op.GetPushOp(privateKey.PubKey.ToBytes()));
            lockOps.Add(OpcodeType.OP_CHECKSIGVERIFY);

            foreach (var op in partialScript.ToOps())
            {
                lockOps.Add(OpcodeType.OP_DROP);
            }
            lockOps.Add(OpcodeType.OP_TRUE);

            Script lockScript = new Script(lockOps.ToArray());

            // Создаем P2SH скрипт
            Script p2shScript = PayToScriptHashTemplate.Instance.GenerateScriptPubKey(lockScript);

            // Создаем транзакцию
            Transaction tx = network.CreateTransaction();

            // Если есть предыдущий P2SH вход, добавляем его
            if (p2shCoin != null)
            {
                tx.Inputs.Add(new TxIn(p2shCoin.Outpoint));
            }

            // Добавляем выход с P2SH скриптом
            TxOut p2shOutput = new TxOut(Money.Satoshis(100000), p2shScript);
            tx.Outputs.Add(p2shOutput);

            // Финансируем транзакцию
            await FundTransactionAsync(tx);

            // Подписываем транзакцию
            if (p2shCoin != null)
            {
                // Получаем подписываемый хэш
                uint256 hash = tx.GetSignatureHash(lastLockScript, 0, SigHash.All, p2shOutput);

                // Создаем подпись
                ECDSASignature ecdsaSignature = privateKey.Sign(hash);
                TransactionSignature signature = new TransactionSignature(ecdsaSignature, SigHash.All);

                // Создаем unlocking script
                List<Op> unlockOps = new List<Op>(lastPartialScript.ToOps());
                unlockOps.Add(Op.GetPushOp(signature.ToBytes()));
                unlockOps.Add(Op.GetPushOp(lastLockScript.ToBytes()));

                tx.Inputs[0].ScriptSig = new Script(unlockOps.ToArray());
            }

            // Подписываем входы UTXO
            SignTransactionInputs(tx);

            // Обновляем UTXO
            UpdateWallet(tx);

            // Добавляем транзакцию в список
            txs.Add(tx);

            // Готовимся к следующей итерации
            p2shCoin = new Coin(new OutPoint(tx.GetHash(), 0), tx.Outputs[0]);
            lastLockScript = lockScript;
            lastPartialScript = partialScript;
        }

        // Создаем финальную транзакцию
        Transaction finalTx = network.CreateTransaction();
        finalTx.Inputs.Add(new TxIn(p2shCoin.Outpoint));
        finalTx.Outputs.Add(new TxOut(Money.Satoshis(100000), receiver));

        // Финансируем транзакцию
        await FundTransactionAsync(finalTx);

        // Подписываем входы UTXO
        SignTransactionInputs(finalTx);

        // Создаем unlocking script для финальной транзакции
        {
            // Получаем подписываемый хэш
            uint256 hash = finalTx.GetSignatureHash(lastLockScript, 0, SigHash.All, new TxOut(Money.Satoshis(100000), receiver));

            // Создаем подпись
            ECDSASignature ecdsaSignature = privateKey.Sign(hash);
            TransactionSignature signature = new TransactionSignature(ecdsaSignature, SigHash.All);

            // Создаем unlocking script
            List<Op> unlockOps = new List<Op>(lastPartialScript.ToOps());
            unlockOps.Add(Op.GetPushOp(signature.ToBytes()));
            unlockOps.Add(Op.GetPushOp(lastLockScript.ToBytes()));

            finalTx.Inputs[0].ScriptSig = new Script(unlockOps.ToArray());
        }

        // Обновляем UTXO
        UpdateWallet(finalTx);

        // Добавляем финальную транзакцию в список
        txs.Add(finalTx);

        // Отправляем все транзакции
        foreach (var txToSend in txs)
        {
            await BroadcastTransactionAsync(txToSend);
        }

        // Возвращаем ID транзакции
        return finalTx.GetHash().ToString();
    }

    private async Task FundTransactionAsync(Transaction tx)
    {
        // Добавляем входы
        Money totalInput = Money.Zero;
        foreach (var coin in utxos)
        {
            tx.Inputs.Add(new TxIn(coin.Outpoint));
            totalInput += coin.Amount;
            if (totalInput >= tx.TotalOut + Money.Satoshis(FEE_PER_KB))
            {
                break;
            }
        }

        // Проверяем, достаточно ли средств
        if (totalInput < tx.TotalOut + Money.Satoshis(FEE_PER_KB))
        {
            throw new Exception("Недостаточно средств для финансирования транзакции.");
        }

        // Добавляем выход для сдачи
        Money change = totalInput - tx.TotalOut - Money.Satoshis(FEE_PER_KB);
        if (change > Money.Zero)
        {
            tx.Outputs.Add(new TxOut(change, address));
        }
    }

    private void SignTransactionInputs(Transaction tx)
    {
        // Подписываем входы UTXO
        for (int i = 0; i < tx.Inputs.Count; i++)
        {
            var txIn = tx.Inputs[i];
            var coin = utxos.FirstOrDefault(c => c.Outpoint == txIn.PrevOut);
            if (coin != null)
            {
                var scriptPubKey = coin.TxOut.ScriptPubKey;
                var hash = tx.GetSignatureHash(scriptPubKey, i, SigHash.All);

                var ecdsaSignature = privateKey.Sign(hash);
                var transactionSignature = new TransactionSignature(ecdsaSignature, SigHash.All);

                txIn.ScriptSig = PayToPubkeyHashTemplate.Instance.GenerateScriptSig(transactionSignature, privateKey.PubKey);
            }
        }
    }

    private void UpdateWallet(Transaction tx)
    {
        // Удаляем использованные монеты
        var spentOutpoints = tx.Inputs.Select(input => input.PrevOut).ToHashSet();
        utxos = utxos.Where(coin => !spentOutpoints.Contains(coin.Outpoint)).ToList();

        // Добавляем новые UTXO из выходов на наш адрес
        for (int i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            if (output.ScriptPubKey == address.ScriptPubKey)
            {
                var newCoin = new Coin(new OutPoint(tx.GetHash(), i), output);
                utxos.Add(newCoin);
            }
        }
    }

    private async Task BroadcastTransactionAsync(Transaction tx)
    {
        // API-эндпоинт для отправки транзакции
        string url = "https://wallet-api.dogeord.io/tx/broadcast";

        // Подготовка данных запроса
        var payload = new { rawTx = tx.ToHex() };
        var jsonPayload = JsonConvert.SerializeObject(payload);

        using (HttpClient client = new HttpClient())
        {
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            string result = await response.Content.ReadAsStringAsync();

            dynamic res = JsonConvert.DeserializeObject(result);

            if (res.status != "1")
            {
                throw new Exception($"Ошибка отправки транзакции: {res.message}");
            }
            else
            {
                Console.WriteLine("Транзакция успешно отправлена. TXID: " + res.result);
            }
        }
    }

    private byte[] EncodeNumber(int number)
    {
        if (number == 0) return new byte[0];
        return new Script(Op.GetPushOp(number)).ToOps().First().PushData;
    }
}
