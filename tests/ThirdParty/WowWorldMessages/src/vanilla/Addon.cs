using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using InfoBlockType = OneOf.OneOf<Addon.InfoBlockAvailable, InfoBlock>;
using KeyVersionType = OneOf.OneOf<Addon.KeyVersionEight, Addon.KeyVersionFive, Addon.KeyVersionFour, Addon.KeyVersionNine, Addon.KeyVersionOne, Addon.KeyVersionSeven, Addon.KeyVersionSix, Addon.KeyVersionThree, Addon.KeyVersionTwo, KeyVersion>;
using UrlInfoType = OneOf.OneOf<Addon.UrlInfoAvailable, UrlInfo>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class Addon {
    public class InfoBlockAvailable {
        public required KeyVersionType KeyVersion { get; set; }
        internal KeyVersion KeyVersionValue => KeyVersion.Match(
            _ => Vanilla.KeyVersion.Eight,
            _ => Vanilla.KeyVersion.Five,
            _ => Vanilla.KeyVersion.Four,
            _ => Vanilla.KeyVersion.Nine,
            _ => Vanilla.KeyVersion.One,
            _ => Vanilla.KeyVersion.Seven,
            _ => Vanilla.KeyVersion.Six,
            _ => Vanilla.KeyVersion.Three,
            _ => Vanilla.KeyVersion.Two,
            v => v
        );
        public required uint UpdateAvailableFlag { get; set; }
    }
    public class KeyVersionEight {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionFive {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionFour {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionNine {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionOne {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionSeven {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionSix {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionThree {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class KeyVersionTwo {
        public const int PublicKeyLength = 256;
        public required byte[] PublicKey { get; set; }
    }
    public class UrlInfoAvailable {
        public required string Url { get; set; }
    }
    public required Vanilla.AddonType AddonType { get; set; }
    public required InfoBlockType InfoBlock { get; set; }
    internal InfoBlock InfoBlockValue => InfoBlock.Match(
        _ => Vanilla.InfoBlock.Available,
        v => v
    );
    public required UrlInfoType UrlInfo { get; set; }
    internal UrlInfo UrlInfoValue => UrlInfo.Match(
        _ => Vanilla.UrlInfo.Available,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)AddonType, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)InfoBlockValue, cancellationToken).ConfigureAwait(false);

        if (InfoBlock.Value is Addon.InfoBlockAvailable infoBlockAvailable) {
            await w.WriteByte((byte)infoBlockAvailable.KeyVersionValue, cancellationToken).ConfigureAwait(false);

            if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionOne keyVersionOne) {
                foreach (var v in keyVersionOne.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionTwo keyVersionTwo) {
                foreach (var v in keyVersionTwo.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionThree keyVersionThree) {
                foreach (var v in keyVersionThree.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionFour keyVersionFour) {
                foreach (var v in keyVersionFour.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionFive keyVersionFive) {
                foreach (var v in keyVersionFive.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionSix keyVersionSix) {
                foreach (var v in keyVersionSix.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionSeven keyVersionSeven) {
                foreach (var v in keyVersionSeven.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionEight keyVersionEight) {
                foreach (var v in keyVersionEight.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionNine keyVersionNine) {
                foreach (var v in keyVersionNine.PublicKey) {
                    await w.WriteByte(v, cancellationToken).ConfigureAwait(false);
                }

            }

            await w.WriteUInt(infoBlockAvailable.UpdateAvailableFlag, cancellationToken).ConfigureAwait(false);

        }

        await w.WriteByte((byte)UrlInfoValue, cancellationToken).ConfigureAwait(false);

        if (UrlInfo.Value is Addon.UrlInfoAvailable urlInfoAvailable) {
            await w.WriteCString(urlInfoAvailable.Url, cancellationToken).ConfigureAwait(false);

        }

    }

    public static async Task<Addon> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var addonType = (Vanilla.AddonType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        InfoBlockType infoBlock = (Vanilla.InfoBlock)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (infoBlock.Value is Vanilla.InfoBlock.Available) {
            KeyVersionType keyVersion = (Vanilla.KeyVersion)await r.ReadByte(cancellationToken).ConfigureAwait(false);

            if (keyVersion.Value is Vanilla.KeyVersion.One) {
                var publicKey = new byte[KeyVersionOne.PublicKeyLength];
                for (var i = 0; i < KeyVersionOne.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionOne {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Two) {
                var publicKey = new byte[KeyVersionTwo.PublicKeyLength];
                for (var i = 0; i < KeyVersionTwo.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionTwo {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Three) {
                var publicKey = new byte[KeyVersionThree.PublicKeyLength];
                for (var i = 0; i < KeyVersionThree.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionThree {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Four) {
                var publicKey = new byte[KeyVersionFour.PublicKeyLength];
                for (var i = 0; i < KeyVersionFour.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionFour {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Five) {
                var publicKey = new byte[KeyVersionFive.PublicKeyLength];
                for (var i = 0; i < KeyVersionFive.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionFive {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Six) {
                var publicKey = new byte[KeyVersionSix.PublicKeyLength];
                for (var i = 0; i < KeyVersionSix.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionSix {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Seven) {
                var publicKey = new byte[KeyVersionSeven.PublicKeyLength];
                for (var i = 0; i < KeyVersionSeven.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionSeven {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Eight) {
                var publicKey = new byte[KeyVersionEight.PublicKeyLength];
                for (var i = 0; i < KeyVersionEight.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionEight {
                    PublicKey = publicKey,
                };
            }
            else if (keyVersion.Value is Vanilla.KeyVersion.Nine) {
                var publicKey = new byte[KeyVersionNine.PublicKeyLength];
                for (var i = 0; i < KeyVersionNine.PublicKeyLength; ++i) {
                    publicKey[i] = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                }

                keyVersion = new KeyVersionNine {
                    PublicKey = publicKey,
                };
            }

            var updateAvailableFlag = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            infoBlock = new InfoBlockAvailable {
                KeyVersion = keyVersion,
                UpdateAvailableFlag = updateAvailableFlag,
            };
        }

        UrlInfoType urlInfo = (Vanilla.UrlInfo)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (urlInfo.Value is Vanilla.UrlInfo.Available) {
            var url = await r.ReadCString(cancellationToken).ConfigureAwait(false);

            urlInfo = new UrlInfoAvailable {
                Url = url,
            };
        }

        return new Addon {
            AddonType = addonType,
            InfoBlock = infoBlock,
            UrlInfo = urlInfo,
        };
    }

    internal int Size() {
        var size = 0;

        // addon_type: Generator.Generated.DataTypeEnum
        size += 1;

        // info_block: Generator.Generated.DataTypeEnum
        size += 1;

        if (InfoBlock.Value is Addon.InfoBlockAvailable infoBlockAvailable) {
            // key_version: Generator.Generated.DataTypeEnum
            size += 1;

            if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionOne keyVersionOne) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionOne.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionTwo keyVersionTwo) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionTwo.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionThree keyVersionThree) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionThree.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionFour keyVersionFour) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionFour.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionFive keyVersionFive) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionFive.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionSix keyVersionSix) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionSix.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionSeven keyVersionSeven) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionSeven.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionEight keyVersionEight) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionEight.PublicKey.Sum(e => 1);

            }
            else if (infoBlockAvailable.KeyVersion.Value is Addon.KeyVersionNine keyVersionNine) {
                // public_key: Generator.Generated.DataTypeArray
                size += keyVersionNine.PublicKey.Sum(e => 1);

            }

            // update_available_flag: Generator.Generated.DataTypeInteger
            size += 4;

        }

        // url_info: Generator.Generated.DataTypeEnum
        size += 1;

        if (UrlInfo.Value is Addon.UrlInfoAvailable urlInfoAvailable) {
            // url: Generator.Generated.DataTypeCstring
            size += urlInfoAvailable.Url.Length + 1;

        }

        return size;
    }

}

